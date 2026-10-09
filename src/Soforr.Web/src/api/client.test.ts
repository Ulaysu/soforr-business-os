import { describe, expect, it } from 'vitest'
import { createApiClient } from './client'

interface Recorded {
  method: string
  path: string
  url: string
  headers: Headers
  credentials: RequestCredentials | undefined
}

/** A fake fetch that records what the client sends and answers from a small table. */
function fakeServer() {
  const calls: Recorded[] = []
  let tokens = 0

  const fetchFake = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = new Request(input, init)
    const path = new URL(request.url, 'http://localhost').pathname
    calls.push({
      method: request.method,
      path,
      url: request.url,
      headers: request.headers,
      credentials: init?.credentials ?? request.credentials,
    })

    if (path === '/api/v1/auth/antiforgery') {
      tokens += 1
      return Response.json({ headerName: 'X-CSRF-TOKEN', token: `token-${tokens}` })
    }
    return Response.json({ items: [] })
  }

  return { calls, fetchFake }
}

describe('the API client', () => {
  it('sends the session cookie and no antiforgery header on a read', async () => {
    const { calls, fetchFake } = fakeServer()
    const { client } = createApiClient({
      baseUrl: 'http://localhost',
      fetch: fetchFake as typeof fetch,
    })

    await client.GET('/api/v1/products')

    expect(calls).toHaveLength(1)
    expect(calls[0]?.path).toBe('/api/v1/products')
    expect(calls[0]?.headers.has('X-CSRF-TOKEN')).toBe(false)
    expect(calls[0]?.credentials).toBe('include')
  })

  it('fetches the antiforgery token once and sends it on every unsafe request', async () => {
    const { calls, fetchFake } = fakeServer()
    const { client } = createApiClient({
      baseUrl: 'http://localhost',
      fetch: fetchFake as typeof fetch,
    })

    await client.POST('/api/v1/customers', { body: { name: 'Awa Jallow' } })
    await client.POST('/api/v1/customers', { body: { name: 'Bakary Ceesay' } })

    const posts = calls.filter((call) => call.method === 'POST')
    expect(calls.filter((call) => call.path === '/api/v1/auth/antiforgery')).toHaveLength(1)
    expect(posts.map((call) => call.headers.get('X-CSRF-TOKEN'))).toEqual(['token-1', 'token-1'])
  })

  it('fetches a new token after the identity changes', async () => {
    const { calls, fetchFake } = fakeServer()
    const { client } = createApiClient({
      baseUrl: 'http://localhost',
      fetch: fetchFake as typeof fetch,
    })

    await client.POST('/api/v1/auth/login', {
      body: { email: 'a@example.test', password: 'a long password here' },
    })
    await client.POST('/api/v1/customers', { body: { name: 'Awa Jallow' } })

    const posts = calls.filter((call) => call.method === 'POST')
    expect(posts.map((call) => call.headers.get('X-CSRF-TOKEN'))).toEqual(['token-1', 'token-2'])
  })

  it('forgets the token on request', async () => {
    const { calls, fetchFake } = fakeServer()
    const { client, resetAntiforgery } = createApiClient({
      baseUrl: 'http://localhost',
      fetch: fetchFake as typeof fetch,
    })

    await client.POST('/api/v1/customers', { body: { name: 'Awa Jallow' } })
    resetAntiforgery()
    await client.POST('/api/v1/customers', { body: { name: 'Bakary Ceesay' } })

    expect(calls.filter((call) => call.path === '/api/v1/auth/antiforgery')).toHaveLength(2)
  })

  it('uses the base url when one is given', async () => {
    const { calls, fetchFake } = fakeServer()
    const { client } = createApiClient({
      baseUrl: 'https://api.example.test',
      fetch: fetchFake as typeof fetch,
    })

    await client.GET('/api/v1/products')

    expect(calls[0]?.path).toBe('/api/v1/products')
    expect(calls[0]?.url).toBe('https://api.example.test/api/v1/products')
  })
})
