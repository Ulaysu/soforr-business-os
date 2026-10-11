import { computed, inject, reactive, readonly, type InjectionKey } from 'vue'
import type { ApiClient } from '../api/client'
import { networkProblem, toProblem, type ApiProblem } from '../api/problem'

export type Role = 'Owner' | 'Manager' | 'Staff'

export interface Account {
  userId: string
  organizationId: string | null
  role: Role | null
}

type Status = 'unknown' | 'anonymous' | 'signed-in'

/**
 * Who is signed in, as the server says. The server owns the session (an HttpOnly cookie; nothing about it is stored in the
 * browser) and decides what each person may do; this only remembers the answer of `GET /auth/me` so screens can adapt.
 * A sign-in is never remembered across a reload: the page asks the server again.
 */
export function createSession(api: ApiClient) {
  const state = reactive<{ status: Status; account: Account | null }>({
    status: 'unknown',
    account: null,
  })
  let loading: Promise<void> | null = null

  function setSignedIn(account: Account) {
    state.account = account
    state.status = 'signed-in'
  }

  function setAnonymous() {
    state.account = null
    state.status = 'anonymous'
  }

  /** Finds out whether there is a session. Asked once; later calls reuse the answer until the session changes. */
  function load(): Promise<void> {
    if (state.status !== 'unknown') {
      return Promise.resolve()
    }
    loading ??= (async () => {
      try {
        const { data, response } = await api.GET('/api/v1/auth/me')
        if (response.ok && data) {
          setSignedIn(data as Account)
        } else {
          setAnonymous()
        }
      } catch {
        // The server cannot be reached: treat as not signed in; the sign-in screen reports connection problems itself.
        setAnonymous()
      } finally {
        loading = null
      }
    })()
    return loading
  }

  /** Returns null on success, or what went wrong. */
  async function signIn(email: string, password: string): Promise<ApiProblem | null> {
    try {
      const { response, error } = await api.POST('/api/v1/auth/login', {
        body: { email, password },
      })
      if (!response.ok) {
        return toProblem(error, response.status)
      }
      state.status = 'unknown'
      await load()
      return state.status === 'signed-in' ? null : toProblem(undefined, 401)
    } catch {
      return networkProblem()
    }
  }

  async function signOut(): Promise<void> {
    try {
      await api.POST('/api/v1/auth/logout')
    } finally {
      setAnonymous()
    }
  }

  /** The server ended the session (401 on a call): forget it locally. */
  function expire() {
    if (state.status === 'signed-in') {
      setAnonymous()
    }
  }

  return {
    state: readonly(state),
    isSignedIn: computed(() => state.status === 'signed-in'),
    load,
    signIn,
    signOut,
    expire,
  }
}

export type Session = ReturnType<typeof createSession>

/** The session the application provides to its screens. */
export const sessionKey: InjectionKey<Session> = Symbol('session')

export function useSession(): Session {
  const session = inject(sessionKey)
  if (!session) {
    throw new Error('No session was provided to the application.')
  }
  return session
}

/**
 * Whether to show the controls that change products. This only decides what is shown (the server refuses everyone else with
 * 403 whatever the screen does): Owner and Manager manage products, Staff only look.
 */
export function canManageProducts(role: Role | null | undefined): boolean {
  return role === 'Owner' || role === 'Manager'
}

/**
 * Whether to show the stock adjustment form and the movement history. As with products, this only decides what is shown (the server
 * answers 403 to everyone else): Owner and Manager adjust stock and read the history, Staff only look at the levels.
 */
export function canManageStock(role: Role | null | undefined): boolean {
  return role === 'Owner' || role === 'Manager'
}
