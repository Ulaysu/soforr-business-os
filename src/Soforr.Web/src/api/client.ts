import createClient, { type Middleware } from 'openapi-fetch'
import type { components, paths } from './schema'

type AntiforgeryToken = components['schemas']['AntiforgeryTokenResponse']

const unsafeMethods = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

// Requests after which the identity changes, so the antiforgery token (bound to the identity) must be fetched again.
const identityChanging = ['/api/v1/auth/login', '/api/v1/auth/logout', '/api/v1/auth/register']

export interface ApiClientOptions {
  /** Where the API is. Empty (the default) means the page's own origin: the dev proxy and production are same-site (ADR 0002). */
  baseUrl?: string
  /** The fetch to use; defaults to the global one. Tests pass their own. */
  fetch?: typeof fetch
  /**
   * Called when the API answers 401 to a call other than signing in: the session ended (it expired or was revoked), so the
   * application sends the user to sign in again.
   */
  onUnauthorized?: () => void
}

/**
 * The typed API client. Its types are generated from the API's OpenAPI document (`npm run api:types`), so a request
 * or a response that no longer matches the API fails to compile. It carries the session cookie (the only credential,
 * ADR 0002) and adds the antiforgery header to every unsafe request, fetching the token when it has none.
 *
 * It contains no business rule: it moves requests and responses, nothing more.
 */
export function createApiClient(options: ApiClientOptions = {}) {
  const baseUrl = options.baseUrl ?? ''
  const fetchImpl = options.fetch ?? ((...args: Parameters<typeof fetch>) => fetch(...args))
  let token: AntiforgeryToken | null = null

  async function antiforgery(): Promise<AntiforgeryToken> {
    if (token) {
      return token
    }
    const response = await fetchImpl(`${baseUrl}/api/v1/auth/antiforgery`, {
      credentials: 'include',
    })
    if (!response.ok) {
      throw new Error(`The antiforgery token could not be fetched (${response.status}).`)
    }
    token = (await response.json()) as AntiforgeryToken
    return token
  }

  const antiforgeryMiddleware: Middleware = {
    async onRequest({ request }) {
      if (unsafeMethods.has(request.method)) {
        const { headerName, token: value } = await antiforgery()
        request.headers.set(headerName, value)
      }
      return request
    },
    async onResponse({ request, response }) {
      const path = new URL(request.url, 'http://localhost').pathname
      if (identityChanging.includes(path)) {
        token = null
      }
      if (response.status === 401 && path !== '/api/v1/auth/login') {
        options.onUnauthorized?.()
      }
      return response
    },
  }

  const client = createClient<paths>({ baseUrl, credentials: 'include', fetch: fetchImpl })
  client.use(antiforgeryMiddleware)

  return {
    client,
    /** Forget the cached antiforgery token, for example after a 400 that says it is invalid. */
    resetAntiforgery() {
      token = null
    },
  }
}

export type ApiClient = ReturnType<typeof createApiClient>['client']
