import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { aProduct, createFakeServer, type FakeUser } from '../test/fakeServer'
import { openApp } from '../test/openApp'

const owner: FakeUser = {
  email: 'owner@example.test',
  password: 'a long password here',
  role: 'Owner',
}
const manager: FakeUser = {
  email: 'manager@example.test',
  password: 'a long password here',
  role: 'Manager',
}
const staff: FakeUser = {
  email: 'staff@example.test',
  password: 'another long password',
  role: 'Staff',
}

const rice = aProduct({
  id: '00000000-0000-0000-0000-000000000011',
  sku: 'RICE-25',
  name: 'Rice 25kg',
  reorderPoint: 10,
})
const oil = aProduct({
  id: '00000000-0000-0000-0000-000000000012',
  sku: 'OIL-5',
  name: 'Cooking oil 5l',
  reorderPoint: 5,
})
const salt = aProduct({
  id: '00000000-0000-0000-0000-000000000013',
  sku: 'SALT-1',
  name: 'Salt 1kg',
  reorderPoint: null,
  reorderLeadTimeDays: null,
})

let open: Awaited<ReturnType<typeof openApp>> | undefined

afterEach(() => {
  open?.unmount()
  open = undefined
  vi.useRealTimers()
})

async function openAs(user: FakeUser, path: string) {
  const server = createFakeServer({ users: [owner, manager, staff], products: [rice, oil, salt] })
  server.stock.set(rice.id, 4) // below its reorder point of 10
  server.stock.set(oil.id, 20)
  server.startSession(user.email)
  open = await openApp(server, path)
  return { server, app: open }
}

describe('the stock list', () => {
  it('shows what is on hand for every active product, and marks the low ones', async () => {
    const { app } = await openAs(owner, '/stock')

    const rows = app.allByTestId('stock-row')
    expect(rows.map((row) => row.querySelector('td:nth-child(2)')?.textContent)).toEqual([
      'Cooking oil 5l',
      'Rice 25kg',
      'Salt 1kg',
    ])
    expect(rows.map((row) => row.querySelector('[data-testid="quantity"]')?.textContent)).toEqual([
      '20',
      '4',
      '0',
    ])
    expect(rows[1]?.textContent).toContain('Low')
    expect(rows[0]?.textContent).toContain('OK')
  })

  it('searches after a short pause', async () => {
    const { server, app } = await openAs(owner, '/stock')
    server.requests.length = 0

    vi.useFakeTimers()
    await app.type('search', 'rice')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()

    expect(
      server.requests.filter((request) => request.path === '/api/v1/inventory/stock'),
    ).toHaveLength(1)
    expect(app.allByTestId('stock-row')).toHaveLength(1)
  })

  it('does not let an older search replace the latest stock results', async () => {
    const server = createFakeServer({ users: [owner, manager, staff], products: [rice, oil, salt] })
    server.startSession(owner.email)
    const original = server.fetch
    let release: (() => void) | undefined
    const gate = new Promise<void>((resolve) => (release = resolve))
    const slow = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = new Request(input, init)
      const url = new URL(request.url)
      const response = await original(input, init)
      if (
        url.pathname === '/api/v1/inventory/stock' &&
        url.searchParams.get('q') === 'rice'
      ) {
        await gate
      }
      return response
    }) as typeof fetch
    open = await openApp({ ...server, fetch: slow }, '/stock')

    vi.useFakeTimers()
    await open.type('search', 'rice')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    await open.type('search', 'oil')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()

    expect(open.allByTestId('stock-row')).toHaveLength(1)
    expect(open.text()).toContain('Cooking oil 5l')
    expect(open.text()).not.toContain('Rice 25kg')

    release?.()
    await flushPromises()
    expect(open.allByTestId('stock-row')).toHaveLength(1)
    expect(open.text()).toContain('Cooking oil 5l')
    expect(open.text()).not.toContain('Rice 25kg')
  })

  it("can show only what is low, using the server's own list", async () => {
    const { server, app } = await openAs(owner, '/stock')

    await app.check('lowOnly', true)

    expect(server.requests.at(-1)?.path).toBe('/api/v1/inventory/low-stock')
    expect(
      app.allByTestId('stock-row').map((row) => row.querySelector('td:nth-child(2)')?.textContent),
    ).toEqual(['Rice 25kg'])
  })

  it('opens the details of a product', async () => {
    const { app } = await openAs(owner, '/stock')

    await app.click('Details of Rice 25kg')

    expect(app.router.currentRoute.value.path).toBe(`/stock/${rice.id}`)
    expect(app.byTestId('on-hand')?.textContent).toBe('4')
  })
})

describe('adjusting the stock', () => {
  it('adds stock with a note, shows the new quantity and the movement', async () => {
    const { server, app } = await openAs(manager, `/stock/${rice.id}`)

    await app.type('quantityChange', '6')
    await app.type('note', 'Counted the shelf')
    await app.submit()

    expect(app.byTestId('on-hand')?.textContent).toBe('10')
    expect(app.byTestId('done')?.textContent).toContain('adjusted')
    expect(server.stock.get(rice.id)).toBe(10)
    const rows = app.allByTestId('movement-row')
    expect(rows).toHaveLength(1)
    expect(rows[0]?.textContent).toContain('+6')
    expect(rows[0]?.textContent).toContain('Manual adjustment')
    expect(rows[0]?.textContent).toContain('Counted the shelf')
    // The form is empty again for the next adjustment.
    expect(app.field('quantityChange').value).toBe('')
  })

  it('asks for a note and a change before sending anything', async () => {
    const { server, app } = await openAs(owner, `/stock/${rice.id}`)
    server.requests.length = 0

    await app.type('quantityChange', '3')
    await app.submit()
    expect(app.byTestId('error-note')?.textContent).toContain('note is required')

    await app.type('quantityChange', '0')
    await app.type('note', 'x')
    await app.submit()
    expect(app.byTestId('error-quantityChange')?.textContent).toContain('Enter how much')
    expect(server.requests.some((request) => request.method === 'POST')).toBe(false)
  })

  it("shows the server's refusal when removing more than there is, and changes nothing", async () => {
    const { server, app } = await openAs(owner, `/stock/${rice.id}`)

    await app.type('quantityChange', '-50')
    await app.type('note', 'Damaged')
    await app.submit()

    expect(app.byTestId('form-error')?.textContent).toContain('not enough stock')
    expect(server.stock.get(rice.id)).toBe(4)
    expect(app.byTestId('on-hand')?.textContent).toBe('4')
  })

  it('keeps the same adjustment id when a try is lost, so trying again cannot adjust twice', async () => {
    const server = createFakeServer({ users: [owner], products: [rice] })
    server.stock.set(rice.id, 4)
    server.startSession(owner.email)
    const original = server.fetch
    let lose = true
    const lossy = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const response = await original(input, init)
      if (
        lose &&
        new URL(input instanceof Request ? input.url : String(input)).pathname ===
          '/api/v1/inventory/adjustments'
      ) {
        lose = false
        // The server recorded it, then the answer was lost on the way back.
        throw new TypeError('network down')
      }
      return response
    }) as typeof fetch
    open = await openApp({ ...server, fetch: lossy }, `/stock/${rice.id}`)

    await open.type('quantityChange', '6')
    await open.type('note', 'Counted the shelf')
    await open.submit()
    expect(open.byTestId('form-error')?.textContent).toContain('safe to try again')

    await open.submit()

    expect(server.stock.get(rice.id)).toBe(10) // applied once
    const posts = server.requests.filter(
      (request) => request.path === '/api/v1/inventory/adjustments',
    )
    expect(posts).toHaveLength(2)
    expect((posts[0]?.body as { adjustmentId: string }).adjustmentId).toBe(
      (posts[1]?.body as { adjustmentId: string }).adjustmentId,
    )
    expect(server.adjustments.size).toBe(1)
    await vi.waitFor(() => expect(open?.byTestId('on-hand')?.textContent).toBe('10'))
  })

  it('uses a new adjustment id for the next adjustment', async () => {
    const { server, app } = await openAs(owner, `/stock/${rice.id}`)

    await app.type('quantityChange', '1')
    await app.type('note', 'First')
    await app.submit()
    await app.type('quantityChange', '2')
    await app.type('note', 'Second')
    await app.submit()

    const ids = server.requests
      .filter((request) => request.path === '/api/v1/inventory/adjustments')
      .map((request) => (request.body as { adjustmentId: string }).adjustmentId)
    expect(new Set(ids).size).toBe(2)
    expect(server.stock.get(rice.id)).toBe(7)
  })
})

describe('who may adjust stock', () => {
  it('lets Staff look at the levels but shows no form and no history', async () => {
    const { server, app } = await openAs(staff, `/stock/${rice.id}`)

    expect(app.byTestId('on-hand')?.textContent).toBe('4')
    expect(app.root.querySelector('form')).toBeNull()
    expect(app.text()).toContain('look at the stock but not change it')
    expect(app.text()).not.toContain('History')
    expect(server.requests.some((request) => request.path.endsWith('/movements'))).toBe(false)
  })

  it('says so when the product does not exist', async () => {
    const { app } = await openAs(owner, '/stock/00000000-0000-0000-0000-0000000000ff')

    expect(app.byTestId('stock-error')?.textContent).toContain('was not found')
  })
})

describe('moving between products', () => {
  function seedMovement(
    server: ReturnType<typeof createFakeServer>,
    productId: string,
    note: string,
  ) {
    server.movements.set(productId, [
      {
        id: crypto.randomUUID(),
        quantityChange: 3,
        reason: 'ManualAdjustment',
        sourceType: 'Adjustment',
        sourceId: null,
        actorId: '00000000-0000-0000-0000-000000000001',
        note,
        occurredAtUtc: '2026-06-01T10:00:00Z',
      },
    ])
  }

  it('shows the other product, not the first one, when the route parameter changes', async () => {
    const { server, app } = await openAs(owner, `/stock/${rice.id}`)
    seedMovement(server, rice.id, 'Rice note')
    seedMovement(server, oil.id, 'Oil note')
    await app.router.push(`/stock/${rice.id}`)
    await app.router.push(`/stock/${oil.id}`)
    await vi.waitFor(() => expect(app.byTestId('on-hand')?.textContent).toBe('20'))

    expect(app.text()).toContain('Cooking oil 5l')
    expect(app.text()).not.toContain('Rice 25kg')
    expect(app.text()).toContain('Oil note')
    expect(app.text()).not.toContain('Rice note')
    expect(app.allByTestId('movement-row')).toHaveLength(1)
    // Reorder information is the other product's (5), not rice's (10).
    expect(app.root.querySelector('dl')?.textContent).toMatch(/Reorder at5Supplier/)
  })

  it('does not let a late answer for the first product overwrite the second', async () => {
    const server = createFakeServer({ users: [owner], products: [rice, oil] })
    server.stock.set(rice.id, 4)
    server.stock.set(oil.id, 20)
    server.startSession(owner.email)
    const original = server.fetch
    let release: (() => void) | undefined
    const gate = new Promise<void>((resolve) => (release = resolve))
    const slow = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(input instanceof Request ? input.url : String(input))
      const response = await original(input, init)
      if (url.pathname === `/api/v1/inventory/stock/${rice.id}`) {
        await gate // the answer for rice arrives only after the person moved on to oil
      }
      return response
    }) as typeof fetch
    open = await openApp({ ...server, fetch: slow }, '/')
    const pending = open.router.push(`/stock/${rice.id}`)
    await pending
    await open.router.push(`/stock/${oil.id}`)
    await vi.waitFor(() => expect(open?.byTestId('on-hand')?.textContent).toBe('20'))

    release?.()
    await flushPromises()

    expect(open.byTestId('on-hand')?.textContent).toBe('20')
    expect(open.text()).toContain('Cooking oil 5l')
    expect(open.text()).not.toContain('Rice 25kg')
  })

  it('clears the previous product while the next one is loading', async () => {
    const server = createFakeServer({ users: [owner], products: [rice, oil] })
    server.stock.set(rice.id, 4)
    server.startSession(owner.email)
    const original = server.fetch
    let release: (() => void) | undefined
    const gate = new Promise<void>((resolve) => (release = resolve))
    const slow = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(input instanceof Request ? input.url : String(input))
      if (url.pathname === `/api/v1/inventory/stock/${oil.id}`) await gate
      return original(input, init)
    }) as typeof fetch
    open = await openApp({ ...server, fetch: slow }, `/stock/${rice.id}`)
    expect(open.byTestId('on-hand')?.textContent).toBe('4')

    await open.router.push(`/stock/${oil.id}`)
    await flushPromises()
    expect(open.byTestId('on-hand')).toBeNull()
    expect(open.text()).not.toContain('Rice 25kg')

    release?.()
    await vi.waitFor(() => expect(open?.text()).toContain('Cooking oil 5l'))
  })
})
