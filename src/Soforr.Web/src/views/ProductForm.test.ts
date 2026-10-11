import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { aProduct, createFakeServer, type FakeUser } from '../test/fakeServer'
import { openApp, signInAs } from '../test/openApp'

const owner: FakeUser = {
  email: 'owner@example.test',
  password: 'a long password here',
  role: 'Owner',
}

let open: Awaited<ReturnType<typeof openApp>> | undefined

afterEach(() => {
  open?.unmount()
  open = undefined
})

async function openForm(path: string, products = [aProduct()]) {
  const server = createFakeServer({ users: [owner], products })
  server.startSession(owner.email)
  open = await openApp(server, path)
  return { server, app: open }
}

async function fillValidProduct(
  app: NonNullable<typeof open>,
  overrides: Record<string, string> = {},
) {
  const values = {
    sku: 'SUGAR-50',
    name: 'Sugar 50kg',
    unit: 'bag',
    price: '2300.50',
    cost: '2000',
    reorder: '5',
    lead: '3',
    ...overrides,
  }
  for (const [id, value] of Object.entries(values)) {
    await app.type(id, value)
  }
}

describe('creating a product', () => {
  it('sends the typed details as numbers and returns to the list showing the new product', async () => {
    const { server, app } = await openForm('/products/new', [])

    await fillValidProduct(app)
    await app.submit()

    const post = server.requests.find(
      (request) => request.method === 'POST' && request.path === '/api/v1/products',
    )
    expect(post?.body).toEqual({
      sku: 'SUGAR-50',
      name: 'Sugar 50kg',
      unitOfMeasure: 'bag',
      sellingPrice: 2300.5,
      costPrice: 2000,
      reorderPoint: 5,
      reorderLeadTimeDays: 3,
    })
    expect(app.router.currentRoute.value.name).toBe('products')
    expect(app.allByTestId('product-row').map((row) => row.textContent)).toEqual([
      expect.stringContaining('SUGAR-50'),
    ])
  })

  it('treats an empty reorder point and lead time as not set', async () => {
    const { server, app } = await openForm('/products/new', [])

    await fillValidProduct(app, { cost: '0', reorder: '', lead: '' })
    await app.submit()

    const post = server.requests.find(
      (request) => request.method === 'POST' && request.path === '/api/v1/products',
    )
    expect(post?.body).toMatchObject({
      costPrice: 0,
      reorderPoint: null,
      reorderLeadTimeDays: null,
    })
  })

  it('points out missing and unreadable details without sending anything', async () => {
    const { server, app } = await openForm('/products/new', [])
    server.requests.length = 0

    await app.type('price', 'twelve')
    await app.submit()

    expect(app.byTestId('error-sku')?.textContent).toContain('required')
    expect(app.byTestId('error-name')?.textContent).toContain('required')
    expect(app.byTestId('error-unitOfMeasure')?.textContent).toContain('required')
    expect(app.byTestId('error-sellingPrice')?.textContent).toContain('number')
    expect(app.byTestId('error-costPrice')?.textContent).toContain('Enter 0 if there is none')
    expect(server.requests.some((request) => request.method === 'POST')).toBe(false)
  })

  it("shows the server's own message under the field it is about (a duplicate SKU)", async () => {
    const { app } = await openForm('/products/new', [aProduct({ sku: 'SUGAR-50' })])

    await fillValidProduct(app, { sku: 'sugar-50' })
    await app.submit()

    expect(app.byTestId('error-sku')?.textContent).toContain('already exists')
    // The message is shown once, under the box it is about, not repeated at the bottom.
    expect(app.byTestId('form-error')?.textContent).toContain('need attention')
    expect(app.router.currentRoute.value.name).toBe('product-new')
  })

  it('asks for the reorder point and the lead time together, or neither', async () => {
    const { server, app } = await openForm('/products/new', [])
    server.requests.length = 0

    await fillValidProduct(app, { reorder: '5', lead: '' })
    await app.submit()

    expect(app.byTestId('error-reorderLeadTimeDays')?.textContent).toContain('both')
    expect(server.requests.some((request) => request.method === 'POST')).toBe(false)
  })

  it('asks for the reorder point and the lead time together, or neither', async () => {
    const { server, app } = await openForm('/products/new', [])
    server.requests.length = 0

    await fillValidProduct(app, { reorder: '5', lead: '' })
    await app.submit()

    expect(app.byTestId('error-reorderLeadTimeDays')?.textContent).toContain('both')
    expect(server.requests.some((request) => request.method === 'POST')).toBe(false)
  })

  it('shows what the server rejects even when the screen let it through (the server decides)', async () => {
    const { app } = await openForm('/products/new', [])

    await fillValidProduct(app, { price: '-5' })
    await app.submit()

    expect(app.byTestId('error-sellingPrice')?.textContent).toContain('cannot be negative')
    expect(app.router.currentRoute.value.name).toBe('product-new')
  })
})

describe('editing a product', () => {
  const id = '00000000-0000-0000-0000-000000000001'

  it('loads the current details and saves the changes', async () => {
    const { server, app } = await openForm(`/products/${id}/edit`)

    expect(app.field('sku').value).toBe('RICE-25')
    expect(app.field('price').value).toBe('1250')
    await app.type('price', '1300')
    await app.submit()

    const put = server.requests.find((request) => request.method === 'PUT')
    expect(put?.path).toBe(`/api/v1/products/${id}`)
    expect(put?.body).toMatchObject({ sku: 'RICE-25', sellingPrice: 1300 })
    expect(server.products[0]?.sellingPrice).toBe(1300)
    expect(app.router.currentRoute.value.name).toBe('products')
  })

  it('says so when the product does not exist', async () => {
    const { app } = await openForm('/products/00000000-0000-0000-0000-0000000000ff/edit')

    expect(app.text()).toContain('was not found')
  })

  it('loads the newly selected product when navigating between edit routes', async () => {
    const secondId = '00000000-0000-0000-0000-000000000002'
    const first = aProduct({ id })
    const second = aProduct({
      id: secondId,
      sku: 'OIL-5',
      name: 'Cooking oil 5l',
      sellingPrice: 480,
      costPrice: 400,
    })
    const { server, app } = await openForm(`/products/${id}/edit`, [first, second])
    server.requests.length = 0

    await app.router.push(`/products/${secondId}/edit`)
    await flushPromises()

    expect(app.field('sku').value).toBe('OIL-5')
    expect(app.field('name').value).toBe('Cooking oil 5l')
    expect(app.field('price').value).toBe('480')
    expect(
      server.requests.some(
        (request) => request.method === 'GET' && request.path === `/api/v1/products/${secondId}`,
      ),
    ).toBe(true)
  })

})

describe('the first journey: sign in, create a product', () => {
  it('works from the sign-in page to the product in the list', async () => {
    const server = createFakeServer({ users: [owner] })
    open = await openApp(server, '/')
    expect(open.router.currentRoute.value.name).toBe('sign-in')

    await signInAs(open, owner.email, owner.password)
    expect(open.router.currentRoute.value.name).toBe('products')
    expect(open.byTestId('products-empty')).not.toBeNull()

    await open.click('New product')
    await fillValidProduct(open)
    await open.submit()

    expect(open.router.currentRoute.value.name).toBe('products')
    expect(open.allByTestId('product-row')).toHaveLength(1)
    expect(open.text()).toContain('Sugar 50kg')
  })
})
