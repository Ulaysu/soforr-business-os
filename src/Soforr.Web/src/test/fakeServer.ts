import type { components } from '../api/schema'

type Product = components['schemas']['ProductDto']
type Role = 'Owner' | 'Manager' | 'Staff'

export interface FakeUser {
  email: string
  password: string
  role: Role | null
}

interface Recorded {
  method: string
  path: string
  search: URLSearchParams
  body: unknown
}

/**
 * An in-memory stand-in for the API, just enough for the screens' tests: sessions, products, and the answers the real API gives
 * (problem bodies with `code` and `errors`). It records every request so tests can assert what was sent. It is a test double of
 * the server's behaviour, not a model of its rules: the real rules are tested against the real API.
 */
export function createFakeServer(
  options: { users?: FakeUser[]; products?: Product[]; pageSizeCap?: number } = {},
) {
  const users = options.users ?? [
    { email: 'owner@example.test', password: 'a long password here', role: 'Owner' as Role },
  ]
  const products: Product[] = [...(options.products ?? [])]
  const requests: Recorded[] = []
  let sessionEmail: string | null = null
  const stock = new Map<string, number>()
  const movements = new Map<string, Array<Record<string, unknown>>>()
  const adjustments = new Map<
    string,
    { productId: string; change: number; note: string; newQuantity: number }
  >()
  let nextId = 1000

  const json = (body: unknown, status = 200) => Response.json(body, { status })
  const problem = (
    status: number,
    code: string,
    detail: string,
    errors?: Record<string, string[]>,
  ) => Response.json({ status, code, detail, ...(errors ? { errors } : {}) }, { status })

  const currentUser = () => users.find((user) => user.email === sessionEmail) ?? null
  const canManage = () => currentUser()?.role === 'Owner' || currentUser()?.role === 'Manager'

  function validate(body: Record<string, unknown>): Record<string, string[]> {
    const errors: Record<string, string[]> = {}
    if (!body.sku) errors.sku = ['The SKU is required.']
    if (!body.name) errors.name = ['The name is required.']
    if (typeof body.sellingPrice !== 'number' || body.sellingPrice < 0) {
      errors.sellingPrice = ['The selling price cannot be negative.']
    }
    if (body.costPrice === null || body.costPrice === undefined) {
      errors.costPrice = ['The cost price is required. Enter 0 if there is none.']
    }
    return errors
  }

  const fetchFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = new Request(input, init)
    const url = new URL(request.url, 'http://localhost')
    const method = request.method
    const path = url.pathname
    const text = method === 'GET' ? '' : await request.text()
    const body: unknown = text ? JSON.parse(text) : undefined
    requests.push({ method, path, search: url.searchParams, body })

    if (path === '/api/v1/auth/antiforgery')
      return json({ headerName: 'X-CSRF-TOKEN', token: 'token' })

    if (path === '/api/v1/auth/login' && method === 'POST') {
      const credentials = body as { email: string; password: string }
      const user = users.find(
        (u) => u.email === credentials.email && u.password === credentials.password,
      )
      if (!user)
        return problem(
          401,
          'identity.invalid_credentials',
          'The e-mail address or password is not right.',
        )
      sessionEmail = user.email
      return json({
        userId: '00000000-0000-0000-0000-000000000001',
        organizationId: '00000000-0000-0000-0000-0000000000a1',
        role: user.role,
      })
    }
    if (path === '/api/v1/auth/logout' && method === 'POST') {
      sessionEmail = null
      return new Response(null, { status: 204 })
    }

    const user = currentUser()
    if (!user) return problem(401, 'identity.not_authenticated', 'You need to sign in.')

    if (path === '/api/v1/auth/me') {
      return json({
        userId: '00000000-0000-0000-0000-000000000001',
        organizationId: '00000000-0000-0000-0000-0000000000a1',
        role: user.role,
      })
    }

    if (path === '/api/v1/products' && method === 'GET') {
      const q = (url.searchParams.get('q') ?? '').toLowerCase()
      const includeInactive = url.searchParams.get('includeInactive') === 'true'
      const page = Number(url.searchParams.get('page') ?? 1)
      const pageSize = Number(url.searchParams.get('pageSize') ?? 25)
      const matching = products
        .filter((p) => includeInactive || p.isActive)
        .filter(
          (p) => q === '' || p.sku.toLowerCase().includes(q) || p.name.toLowerCase().includes(q),
        )
        .sort((a, b) => a.name.localeCompare(b.name))
      return json({
        items: matching.slice((page - 1) * pageSize, page * pageSize),
        page,
        pageSize,
        totalCount: matching.length,
      })
    }

    if (path === '/api/v1/products' && method === 'POST') {
      if (!canManage()) return problem(403, 'identity.forbidden', 'You are not allowed to do this.')
      const payload = body as Record<string, unknown>
      const errors = validate(payload)
      if (Object.keys(errors).length > 0)
        return problem(400, 'validation', 'One or more validation errors occurred.', errors)
      if (products.some((p) => p.sku.toLowerCase() === String(payload.sku).toLowerCase())) {
        // As the real API answers: a message and a code, no field.
        return problem(
          409,
          'product.sku_taken',
          'A product with this SKU already exists. SKUs are compared without regard to case.',
        )
      }
      const product: Product = {
        id: `00000000-0000-0000-0000-${String(nextId++).padStart(12, '0')}`,
        sku: String(payload.sku),
        name: String(payload.name),
        unitOfMeasure: String(payload.unitOfMeasure),
        currency: 'GMD',
        sellingPrice: payload.sellingPrice as number,
        costPrice: (payload.costPrice as number | null) ?? null,
        reorderPoint: (payload.reorderPoint as number | null) ?? null,
        reorderLeadTimeDays: (payload.reorderLeadTimeDays as number | null) ?? null,
        isActive: true,
        createdAtUtc: '2026-06-01T10:00:00Z',
        updatedAtUtc: '2026-06-01T10:00:00Z',
      }
      products.push(product)
      return json(product, 201)
    }

    const match = /^\/api\/v1\/products\/([^/]+)(\/deactivate)?$/.exec(path)
    if (match) {
      const product = products.find((p) => p.id === match[1])
      if (!product) return problem(404, 'product.not_found', 'The product was not found.')
      if (match[2] && method === 'POST') {
        if (!canManage())
          return problem(403, 'identity.forbidden', 'You are not allowed to do this.')
        product.isActive = false
        return json(product)
      }
      if (method === 'GET') return json(product)
      if (method === 'PUT') {
        if (!canManage())
          return problem(403, 'identity.forbidden', 'You are not allowed to do this.')
        const payload = body as Record<string, unknown>
        const errors = validate(payload)
        if (Object.keys(errors).length > 0)
          return problem(400, 'validation', 'One or more validation errors occurred.', errors)
        Object.assign(product, payload)
        return json(product)
      }
    }

    // ---- inventory (the shapes and answers of the real API)
    const stockDto = (product: Product) => {
      const quantity = stock.get(product.id) ?? 0
      const point = product.reorderPoint === null ? null : Number(product.reorderPoint)
      return {
        productId: product.id,
        sku: product.sku,
        name: product.name,
        unitOfMeasure: product.unitOfMeasure,
        isActive: product.isActive,
        quantity,
        reorderPoint: point,
        reorderLeadTimeDays: product.reorderLeadTimeDays,
        isLow: product.isActive && point !== null && quantity <= point,
        updatedAtUtc: stock.has(product.id) ? '2026-06-01T10:00:00Z' : null,
      }
    }

    if (path === '/api/v1/inventory/stock' && method === 'GET') {
      const q = (url.searchParams.get('q') ?? '').toLowerCase()
      const page = Number(url.searchParams.get('page') ?? 1)
      const pageSize = Number(url.searchParams.get('pageSize') ?? 25)
      const matching = products
        .filter((p) => p.isActive)
        .filter(
          (p) => q === '' || p.sku.toLowerCase().includes(q) || p.name.toLowerCase().includes(q),
        )
        .sort((a, b) => a.name.localeCompare(b.name))
      return json({
        items: matching.slice((page - 1) * pageSize, page * pageSize).map(stockDto),
        page,
        pageSize,
        totalCount: matching.length,
      })
    }

    if (path === '/api/v1/inventory/low-stock' && method === 'GET') {
      const page = Number(url.searchParams.get('page') ?? 1)
      const pageSize = Number(url.searchParams.get('pageSize') ?? 25)
      const low = products
        .map(stockDto)
        .filter((item) => item.isLow)
        .sort((a, b) => a.quantity - b.quantity)
      const items = low.slice((page - 1) * pageSize, page * pageSize).map((item) => ({
        productId: item.productId,
        sku: item.sku,
        name: item.name,
        unitOfMeasure: item.unitOfMeasure,
        quantity: item.quantity,
        reorderPoint: item.reorderPoint,
        reorderLeadTimeDays: item.reorderLeadTimeDays,
      }))
      return json({ items, page, pageSize, totalCount: low.length })
    }

    const stockMatch = /^\/api\/v1\/inventory\/stock\/([^/]+)(\/movements)?$/.exec(path)
    if (stockMatch && method === 'GET') {
      const product = products.find((p) => p.id === stockMatch[1])
      if (!product) return problem(404, 'product.not_found', 'The product was not found.')
      if (!stockMatch[2]) return json(stockDto(product))
      if (!canManage()) return problem(403, 'identity.forbidden', 'You are not allowed to do this.')
      const items = movements.get(product.id) ?? []
      return json({ items, page: 1, pageSize: 25, totalCount: items.length })
    }

    if (path === '/api/v1/inventory/adjustments' && method === 'POST') {
      if (!canManage()) return problem(403, 'identity.forbidden', 'You are not allowed to do this.')
      const payload = body as {
        productId: string
        quantityChange: number | null
        note: string | null
        adjustmentId: string | null
      }
      const product = products.find((p) => p.id === payload.productId)
      if (!product) return problem(404, 'product.not_found', 'The product was not found.')
      if (!payload.note || payload.note.trim() === '') {
        return problem(400, 'inventory.note_required', 'A note is required.', {
          note: ['A note is required: say why the stock is being changed.'],
        })
      }
      if (!payload.quantityChange) {
        return problem(400, 'inventory.change_required', 'The change is required.', {
          quantityChange: ['The change must not be zero.'],
        })
      }
      const id = payload.adjustmentId ?? crypto.randomUUID()
      const existing = adjustments.get(id)
      if (existing) {
        return json({
          movementId: id,
          adjustmentId: id,
          newQuantity: existing.newQuantity,
          alreadyRecorded: true,
        })
      }
      const current = stock.get(product.id) ?? 0
      if (current + payload.quantityChange < 0) {
        return problem(
          422,
          'stock_level.insufficient_stock',
          'There is not enough stock to remove that much.',
        )
      }
      const next = current + payload.quantityChange
      stock.set(product.id, next)
      adjustments.set(id, {
        productId: product.id,
        change: payload.quantityChange,
        note: payload.note,
        newQuantity: next,
      })
      const list = movements.get(product.id) ?? []
      list.unshift({
        id,
        quantityChange: payload.quantityChange,
        reason: 'ManualAdjustment',
        sourceType: 'Adjustment',
        sourceId: id,
        actorId: '00000000-0000-0000-0000-000000000001',
        note: payload.note,
        occurredAtUtc: '2026-06-01T10:00:00Z',
      })
      movements.set(product.id, list)
      return json(
        { movementId: id, adjustmentId: id, newQuantity: next, alreadyRecorded: false },
        201,
      )
    }

    return problem(404, 'not_found', 'Not found.')
  }

  return {
    fetch: fetchFake as typeof fetch,
    requests,
    products,
    /** What the server holds: quantity per product, and the adjustments it accepted (by id). */
    stock,
    adjustments,
    /** Ends the server-side session without the browser knowing, as an expiry does. */
    expireSession() {
      sessionEmail = null
    },
    /** Pretends the browser already holds a valid session cookie for this user. */
    startSession(email: string) {
      sessionEmail = email
    },
  }
}

export function aProduct(overrides: Partial<Product> = {}): Product {
  return {
    id: '00000000-0000-0000-0000-000000000001',
    sku: 'RICE-25',
    name: 'Rice 25kg',
    unitOfMeasure: 'bag',
    currency: 'GMD',
    sellingPrice: 1250,
    costPrice: 1000,
    reorderPoint: 10,
    reorderLeadTimeDays: 7,
    isActive: true,
    createdAtUtc: '2026-06-01T10:00:00Z',
    updatedAtUtc: '2026-06-01T10:00:00Z',
    ...overrides,
  }
}
