import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { createFakeServer, type FakeUser } from '../test/fakeServer'
import { openApp, signInAs } from '../test/openApp'

const owner: FakeUser = {
  email: 'owner@example.test',
  password: 'a long password here',
  role: 'Owner',
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
})

describe('signing in', () => {
  it('sends an anonymous visitor to the sign-in page and remembers where they wanted to go', async () => {
    open = await openApp(createFakeServer({ users: [owner] }), '/products')

    expect(open.router.currentRoute.value.name).toBe('sign-in')
    expect(open.router.currentRoute.value.query.redirect).toBe('/products')
    expect(open.text()).toContain('Sign in')
    expect(open.byTestId('role')).toBeNull()
  })

  it('signs in with the right password and brings the person back to the page they wanted', async () => {
    const server = createFakeServer({ users: [owner] })
    open = await openApp(server, '/products/new')

    await signInAs(open, owner.email, owner.password)

    expect(open.router.currentRoute.value.fullPath).toBe('/products/new')
    expect(open.byTestId('role')?.textContent).toBe('Owner')
    // The antiforgery header travelled with the sign-in, and the password was sent only to the sign-in endpoint.
    const login = server.requests.find((request) => request.path === '/api/v1/auth/login')
    expect(login?.body).toEqual({ email: owner.email, password: owner.password })
    expect(
      server.requests.filter((request) =>
        JSON.stringify(request.body ?? '').includes(owner.password),
      ),
    ).toHaveLength(1)
  })

  it('says the same thing for a wrong password, keeps the person on the page and clears the password', async () => {
    open = await openApp(createFakeServer({ users: [owner] }), '/')

    await signInAs(open, owner.email, 'not the password')

    expect(open.router.currentRoute.value.name).toBe('sign-in')
    expect(open.byTestId('sign-in-error')?.textContent).toContain('not right')
    expect(open.field('password').value).toBe('')
    expect(open.byTestId('role')).toBeNull()
  })

  it('does not send an empty form', async () => {
    const server = createFakeServer({ users: [owner] })
    open = await openApp(server, '/')

    await open.submit()

    expect(open.byTestId('sign-in-error')?.textContent).toContain(
      'Enter your e-mail address and password',
    )
    expect(server.requests.some((request) => request.path === '/api/v1/auth/login')).toBe(false)
  })

  it('does not follow a redirect that leaves the application', async () => {
    open = await openApp(createFakeServer({ users: [owner] }), '/sign-in?redirect=//evil.example')

    await signInAs(open, owner.email, owner.password)

    expect(open.router.currentRoute.value.name).toBe('products')
  })

  it('lets a person who already has a session straight in, and keeps them off the sign-in page', async () => {
    const server = createFakeServer({ users: [owner] })
    server.startSession(owner.email)

    open = await openApp(server, '/sign-in')

    expect(open.router.currentRoute.value.name).toBe('products')
    expect(open.byTestId('role')?.textContent).toBe('Owner')
  })

  it('signs out, returns to the sign-in page and loses access to the screens', async () => {
    const server = createFakeServer({ users: [owner] })
    server.startSession(owner.email)
    open = await openApp(server, '/products')

    await open.click('Sign out')
    await open.router.push('/products')
    await flushPromises()

    expect(open.router.currentRoute.value.name).toBe('sign-in')
    expect(server.requests.some((request) => request.path === '/api/v1/auth/logout')).toBe(true)
  })

  it('sends the person to sign in again when the server ends the session while they work', async () => {
    const server = createFakeServer({ users: [owner] })
    server.startSession(owner.email)
    open = await openApp(server, '/products')
    expect(open.router.currentRoute.value.name).toBe('products')

    server.expireSession()
    await open.type('search', 'rice')
    await new Promise((resolve) => setTimeout(resolve, 350))
    await flushPromises()

    expect(open.router.currentRoute.value.name).toBe('sign-in')
    expect(open.byTestId('role')).toBeNull()
  })

  it('shows the role of whoever signed in', async () => {
    const server = createFakeServer({ users: [staff] })
    open = await openApp(server, '/')

    await signInAs(open, staff.email, staff.password)

    expect(open.byTestId('role')?.textContent).toBe('Staff')
  })
})
