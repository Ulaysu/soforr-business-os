import { flushPromises } from '@vue/test-utils'
import { createMemoryHistory } from 'vue-router'
import { createApplication } from '../bootstrap'
import type { createFakeServer } from './fakeServer'

type FakeServer = ReturnType<typeof createFakeServer>

/** Mounts the real application (router, session, screens) against a fake server and opens a path, as a browser would. */
export async function openApp(server: FakeServer, path = '/') {
  const { app, router, session } = createApplication({
    history: createMemoryHistory(),
    fetch: server.fetch,
    baseUrl: 'http://localhost',
  })
  const root = document.createElement('div')
  document.body.appendChild(root)
  app.mount(root)
  await router.push(path)
  await router.isReady()
  await flushPromises()

  const api = {
    router,
    session,
    root,
    text: () => root.textContent ?? '',
    byTestId: (id: string) => root.querySelector<HTMLElement>(`[data-testid="${id}"]`),
    allByTestId: (id: string) =>
      Array.from(root.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)),
    field(id: string) {
      const element = root.querySelector<HTMLInputElement>(`#${id}`)
      if (!element) throw new Error(`No field #${id} on ${router.currentRoute.value.fullPath}`)
      return element
    },
    async type(id: string, value: string) {
      const element = api.field(id)
      element.value = value
      element.dispatchEvent(new Event('input'))
      await flushPromises()
    },
    async check(id: string, checked: boolean) {
      const element = api.field(id)
      element.checked = checked
      element.dispatchEvent(new Event('change'))
      await flushPromises()
    },
    button(label: string | RegExp) {
      const matches = (text: string | null | undefined) =>
        typeof label === 'string' ? text?.trim() === label : label.test(text ?? '')
      const match = Array.from(root.querySelectorAll<HTMLElement>('button, a')).find(
        (element) => matches(element.textContent) || matches(element.getAttribute('aria-label')),
      )
      if (!match)
        throw new Error(`No button or link "${label}" on ${router.currentRoute.value.fullPath}`)
      return match
    },
    async click(label: string | RegExp) {
      api.button(label).click()
      await flushPromises()
    },
    async submit(selector = 'form') {
      root.querySelector(selector)?.dispatchEvent(new Event('submit', { cancelable: true }))
      await flushPromises()
    },
    unmount: () => {
      app.unmount()
      root.remove()
    },
  }
  return api
}

export async function signInAs(
  app: Awaited<ReturnType<typeof openApp>>,
  email: string,
  password: string,
) {
  await app.type('email', email)
  await app.type('password', password)
  await app.submit()
}
