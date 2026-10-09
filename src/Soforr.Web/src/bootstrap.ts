import { createApp } from 'vue'
import { createWebHistory, type RouterHistory } from 'vue-router'
import App from './App.vue'
import { apiKey } from './api/context'
import { createApiClient } from './api/client'
import { createAppRouter } from './router'
import { createSession, sessionKey } from './stores/session'

export interface ApplicationOptions {
  /** Defaults to the browser's history; tests use an in-memory one. */
  history?: RouterHistory
  /** Defaults to the global fetch; tests pass a fake server. */
  fetch?: typeof fetch
  baseUrl?: string
}

/**
 * Builds the application: the API client, the session, the router and the root component, wired together. `main.ts` mounts it
 * in the browser; the tests mount the very same application against a fake server.
 */
export function createApplication(options: ApplicationOptions = {}) {
  let onSessionEnded = () => {}
  const { client } = createApiClient({
    baseUrl: options.baseUrl,
    fetch: options.fetch,
    onUnauthorized: () => onSessionEnded(),
  })
  const session = createSession(client)
  const router = createAppRouter(session, options.history ?? createWebHistory())

  // The server ended the session (it expired or was revoked): forget it and ask the person to sign in again.
  onSessionEnded = () => {
    session.expire()
    const current = router.currentRoute.value
    if (!current.meta.public) {
      void router.push({ name: 'sign-in', query: { redirect: current.fullPath } })
    }
  }

  const app = createApp(App)
  app.provide(apiKey, client)
  app.provide(sessionKey, session)
  app.use(router)

  return { app, router, session, client }
}
