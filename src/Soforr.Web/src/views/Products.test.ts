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

let open: Awaited<ReturnType<typeof openApp>> | undefined

afterEach(() => {
  open?.unmount()
  open = undefined
  vi.useRealTimers()
})

function twoProducts() {
  return [
    aProduct({ id: '00000000-0000-0000-0000-000000000011', sku: 'RICE-25', name: 'Rice 25kg' }),
    aProduct({
      id: '00000000-0000-0000-0000-000000000012',
      sku: 'OIL-5',
      name: 'Cooking oil 5l',
      sellingPrice: 480,
      costPrice: 400,
    }),
  ]
}

async function openProducts(user: FakeUser, products = twoProducts()) {
  const server = createFakeServer({ users: [owner, manager, staff], products })
  server.startSession(user.email)
  open = await openApp(server, '/products')
  return { server, app: open }
}

describe('the product list', () => {
  it('lists the products with their prices, units and status', async () => {
    const { app } = await openProducts(owner)

    const rows = app.allByTestId('product-row')
    expect(rows.map((row) => row.querySelector('td:nth-child(2)')?.textContent)).toEqual([
      'Cooking oil 5l',
      'Rice 25kg',
    ])
    expect(rows[1]?.textContent).toContain('RICE-25')
    expect(rows[1]?.textContent).toContain('bag')
    expect(rows[1]?.textContent).toMatch(/1,250(\.00)?/)
    expect(rows[1]?.textContent).toContain('Active')
  })

  it('shows a dash instead of the cost price when the server does not send it', async () => {
    const { app } = await openProducts(manager, [aProduct({ costPrice: null })])

    expect(app.allByTestId('product-row')[0]?.querySelector('td:nth-child(5)')?.textContent).toBe(
      '—',
    )
  })

  it('says so when there are no products, and when a search finds none', async () => {
    const { app } = await openProducts(owner, [])
    expect(app.byTestId('products-empty')?.textContent).toContain('There are no products yet')

    vi.useFakeTimers()
    await app.type('search', 'nothing like this')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()

    expect(app.byTestId('products-empty')?.textContent).toContain('No products match your search')
  })

  it('asks the server to search after a short pause, not on every key, and starts again from the first page', async () => {
    const { server, app } = await openProducts(owner)
    server.requests.length = 0

    vi.useFakeTimers()
    await app.type('search', 'r')
    await app.type('search', 'ri')
    await app.type('search', 'rice')
    expect(server.requests.filter((request) => request.path === '/api/v1/products')).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()

    const searches = server.requests.filter((request) => request.path === '/api/v1/products')
    expect(searches).toHaveLength(1)
    expect(searches[0]?.search.get('q')).toBe('rice')
    expect(searches[0]?.search.get('page')).toBe('1')
    expect(app.allByTestId('product-row')).toHaveLength(1)
  })

  it('shows inactive products only when asked', async () => {
  it('does not let an older search replace the latest results', async () => {
    const server = createFakeServer({ users: [owner], products: twoProducts() })
    server.startSession(owner.email)
    const original = server.fetch
    let release: (() => void) | undefined
    const gate = new Promise<void>((resolve) => (release = resolve))
    const slow = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const request = new Request(input, init)
      const url = new URL(request.url)
      const response = await original(input, init)
      if (url.pathname === '/api/v1/products' && url.searchParams.get('q') === 'rice') {
        await gate
      }
      return response
    }) as typeof fetch
    open = await openApp({ ...server, fetch: slow }, '/products')

    vi.useFakeTimers()
    await open.type('search', 'rice')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    await open.type('search', 'oil')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()

    expect(open.allByTestId('product-row')).toHaveLength(1)
    expect(open.text()).toContain('Cooking oil 5l')
    expect(open.text()).not.toContain('Rice 25kg')

    release?.()
    await flushPromises()
    expect(open.allByTestId('product-row')).toHaveLength(1)
    expect(open.text()).toContain('Cooking oil 5l')
    expect(open.text()).not.toContain('Rice 25kg')
  })

    const { server, app } = await openProducts(owner, [
      ...twoProducts(),
      aProduct({
        id: '00000000-0000-0000-0000-000000000013',
        sku: 'OLD-1',
        name: 'Old stock',
        isActive: false,
      }),
    ])
    expect(app.allByTestId('product-row')).toHaveLength(2)

    await app.check('includeInactive', true)

    expect(server.requests.at(-1)?.search.get('includeInactive')).toBe('true')
    expect(app.allByTestId('product-row')).toHaveLength(3)
    expect(app.text()).toContain('Inactive')
  })

  it('pages through a long list', async () => {
    const many = Array.from({ length: 45 }, (_, index) =>
      aProduct({
        id: `00000000-0000-0000-0000-${String(index + 100).padStart(12, '0')}`,
        sku: `SKU-${String(index).padStart(2, '0')}`,
        name: `Product ${String(index).padStart(2, '0')}`,
      }),
    )
    const { app } = await openProducts(owner, many)
    expect(app.byTestId('page-info')?.textContent).toBe('Page 1 of 3')
    expect(app.allByTestId('product-row')).toHaveLength(20)

    await app.click('Next')
    await app.click('Next')

    expect(app.byTestId('page-info')?.textContent).toBe('Page 3 of 3')
    expect(app.allByTestId('product-row')).toHaveLength(5)
    expect((app.button('Next') as HTMLButtonElement).disabled).toBe(true)
  })

  it('shows what went wrong when the server refuses', async () => {
    const server = createFakeServer({ users: [owner], products: twoProducts() })
    server.startSession(owner.email)
    const original = server.fetch
    const failing = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(new Request(input, init).url)
      if (url.pathname === '/api/v1/products')
        return Response.json({ detail: 'You are not allowed to do this.' }, { status: 403 })
      return original(input, init)
    }) as typeof fetch
    open = await openApp({ ...server, fetch: failing }, '/products')

    expect(open.byTestId('products-error')?.textContent).toContain('not allowed')
  })

  it('shows a connection problem when the server cannot be reached', async () => {
    const server = createFakeServer({ users: [owner], products: twoProducts() })
    server.startSession(owner.email)
    const original = server.fetch
    let down = false
    const flaky = (async (input: RequestInfo | URL, init?: RequestInit) => {
      if (down && new URL(new Request(input, init).url).pathname === '/api/v1/products')
        throw new TypeError('network down')
      return original(input, init)
    }) as typeof fetch
    open = await openApp({ ...server, fetch: flaky }, '/products/new')
    down = true

    await open.router.push('/products')
    await flushPromises()

    expect(open.byTestId('products-error')?.textContent).toContain('could not be reached')
  })
})

describe('who may change products', () => {
  it('shows the controls to an Owner and a Manager', async () => {
    for (const user of [owner, manager]) {
      const { app } = await openProducts(user)
      expect(app.button('New product')).toBeTruthy()
      expect(app.button('Edit')).toBeTruthy()
      expect(app.button(/^Deactivate/)).toBeTruthy()
      app.unmount()
    }
  })

  it('shows Staff the list only', async () => {
    const { app } = await openProducts(staff)

    expect(app.allByTestId('product-row')).toHaveLength(2)
    expect(() => app.button('New product')).toThrow()
    expect(() => app.button('Edit')).toThrow()
    expect(() => app.button(/^Deactivate/)).toThrow()
  })

  it('tells Staff who open the form directly that they cannot change products, and sends nothing', async () => {
    const server = createFakeServer({ users: [staff], products: twoProducts() })
    server.startSession(staff.email)
    open = await openApp(server, '/products/new')

    expect(open.byTestId('not-allowed')?.textContent).toContain(
      'look at products but not change them',
    )
    expect(open.root.querySelector('form')).toBeNull()
  })
})

describe('deactivating a product', () => {
  it('asks first, and does nothing when the person cancels', async () => {
    const { server, app } = await openProducts(owner)
    server.requests.length = 0

    await app.click('Deactivate Rice 25kg')
    expect(app.text()).toContain('Deactivate Rice 25kg?')
    await app.click('Cancel')

    expect(app.text()).not.toContain('Deactivate Rice 25kg?')
    expect(server.requests.some((request) => request.path.endsWith('/deactivate'))).toBe(false)
    expect(server.products.every((product) => product.isActive)).toBe(true)
  })

  it('deactivates after confirmation and removes the product from the list', async () => {
    const { server, app } = await openProducts(owner)

    await app.click('Deactivate Rice 25kg')
    await app.click('Yes, deactivate')

    expect(
      server.requests.some(
        (request) => request.method === 'POST' && request.path.endsWith('/deactivate'),
      ),
    ).toBe(true)
    expect(server.products.find((product) => product.sku === 'RICE-25')?.isActive).toBe(false)
    expect(app.allByTestId('product-row').map((row) => row.textContent)).not.toContain('Rice 25kg')
    expect(app.text()).not.toContain('RICE-25')
  })
})
