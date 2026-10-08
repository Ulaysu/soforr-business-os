import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'
import App from './App.vue'
import { routes } from './router'

async function mountAt(path: string) {
  const router = createRouter({ history: createMemoryHistory(), routes })
  await router.push(path)
  await router.isReady()
  const wrapper = mount(App, { global: { plugins: [router] } })
  await flushPromises()
  return wrapper
}

describe('the application shell', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows the shell and says the server is reachable when the probe answers', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Healthy', { status: 200 })))

    const wrapper = await mountAt('/')

    expect(wrapper.text()).toContain('Soforr Business OS')
    expect(wrapper.get('[data-testid="api-status"]').text()).toBe('Server reachable')
  })

  it('says the server is not reachable when the probe fails, without breaking the page', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('network down')))

    const wrapper = await mountAt('/')

    expect(wrapper.get('[data-testid="api-status"]').text()).toBe('Server not reachable')
    expect(wrapper.text()).toContain('Welcome')
  })

  it('shows a not-found page for an unknown route', async () => {
    const wrapper = await mountAt('/nowhere')

    expect(wrapper.text()).toContain('Page not found')
  })
})
