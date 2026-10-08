import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '../views/HomeView.vue'
import NotFoundView from '../views/NotFoundView.vue'

// Routes are added with their screens. The shell has a home page and a not-found page only; there are no business
// screens yet, and none of the business rules will ever live in the client (CLAUDE.md section 4).
export const routes = [
  { path: '/', name: 'home', component: HomeView },
  { path: '/:pathMatch(.*)*', name: 'not-found', component: NotFoundView },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})
