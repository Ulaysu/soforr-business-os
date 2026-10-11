import { createRouter, type RouterHistory } from 'vue-router'
import type { Session } from '../stores/session'
import NotFoundView from '../views/NotFoundView.vue'
import ProductFormView from '../views/ProductFormView.vue'
import ProductsView from '../views/ProductsView.vue'
import StockProductView from '../views/StockProductView.vue'
import StockView from '../views/StockView.vue'
import SignInView from '../views/SignInView.vue'

// Routes are added with their screens. None of the business rules lives here or in any screen: the server decides what is
// allowed (CLAUDE.md section 4); a screen shows what the server returned and sends what the person typed.
export const routes = [
  { path: '/', redirect: '/products' },
  { path: '/sign-in', name: 'sign-in', component: SignInView, meta: { public: true } },
  { path: '/products', name: 'products', component: ProductsView },
  { path: '/products/new', name: 'product-new', component: ProductFormView },
  { path: '/products/:id/edit', name: 'product-edit', component: ProductFormView, props: true },
  { path: '/stock', name: 'stock', component: StockView },
  { path: '/stock/:id', name: 'stock-product', component: StockProductView, props: true },
  { path: '/:pathMatch(.*)*', name: 'not-found', component: NotFoundView, meta: { public: true } },
]

/**
 * The router. Every screen except the sign-in and not-found pages needs a signed-in person: an anonymous visitor is sent to the
 * sign-in page (and brought back afterwards), a signed-in person who opens the sign-in page goes to the products. This is a
 * convenience only: the server refuses every call without a session (default deny), whatever the router does.
 */
export function createAppRouter(session: Session, history: RouterHistory) {
  const router = createRouter({ history, routes })

  router.beforeEach(async (to) => {
    await session.load()
    const signedIn = session.isSignedIn.value

    if (to.name === 'sign-in' && signedIn) {
      return { name: 'products' }
    }
    if (!to.meta.public && !signedIn) {
      return { name: 'sign-in', query: to.fullPath === '/' ? {} : { redirect: to.fullPath } }
    }
    return true
  })

  return router
}
