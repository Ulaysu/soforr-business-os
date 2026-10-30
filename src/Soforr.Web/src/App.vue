<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { useSession } from './stores/session'
import { useTheme } from './theme'

const session = useSession()
const router = useRouter()
const route = useRoute()
const { theme, toggle } = useTheme()

// Icons are decorative (the label is always visible); 24x24 paths drawn with currentColor.
const links = [
  {
    to: '/products',
    label: 'Products',
    icon: 'M21 7.5l-9-4.5-9 4.5m18 0l-9 4.5m9-4.5v9l-9 4.5m0-9L3 7.5m9 4.5v9M3 7.5v9l9 4.5',
  },
  {
    to: '/stock',
    label: 'Stock',
    icon: 'M3.75 6.75h16.5M3.75 12h16.5m-16.5 5.25H12',
  },
]

// A section stays selected on its sub-pages (for example Products while creating or editing a product).
function isCurrent(to: string) {
  return route.path === to || route.path.startsWith(`${to}/`)
}

const main = ref<HTMLElement | null>(null)

async function signOut() {
  await session.signOut()
  await router.push({ name: 'sign-in' })
}

const quietButton =
  'rounded-md border border-nav-line px-3 py-1.5 text-sm font-medium whitespace-nowrap text-nav-ink motion-safe:transition-colors hover:bg-hover'

function initial() {
  return (session.state.account?.email ?? 'S').charAt(0).toUpperCase()
}
</script>

<template>
  <div :class="['min-h-screen bg-surface text-ink', session.isSignedIn.value && 'lg:flex']">
    <a
      href="#main"
      class="sr-only rounded-md bg-raised px-4 py-2 text-sm font-medium text-ink shadow-pop focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-10"
      @click.prevent="main?.focus()"
      >Skip to content</a
    >
    <aside
      :class="[
        'border-b border-nav-line bg-nav text-nav-ink',
        session.isSignedIn.value &&
          'lg:sticky lg:top-0 lg:flex lg:h-screen lg:w-64 lg:shrink-0 lg:flex-col lg:border-r lg:border-b-0',
      ]"
    >
      <div class="flex items-center justify-between gap-3 px-4 py-3 sm:px-6 lg:px-5 lg:py-6">
        <div class="flex items-center gap-2.5">
          <span
            aria-hidden="true"
            class="grid size-8 place-items-center rounded-lg bg-accent text-base font-bold text-on-accent"
            >S</span
          >
          <span class="text-xl font-semibold tracking-tight whitespace-nowrap">Soforr.</span>
        </div>
        <div v-if="session.isSignedIn.value" class="flex items-center gap-3 lg:hidden">
          <button
            type="button"
            :class="[quietButton, 'grid size-9 place-items-center px-0']"
            :aria-label="theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'"
            @click="toggle"
          >
            <svg
              aria-hidden="true"
              viewBox="0 0 24 24"
              class="size-4"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path
                v-if="theme === 'dark'"
                d="M12 4v2m0 12v2M4 12h2m12 0h2M6.3 6.3l1.4 1.4m8.6 8.6l1.4 1.4m0-11.4l-1.4 1.4M7.7 16.3l-1.4 1.4M12 8.5a3.5 3.5 0 100 7 3.5 3.5 0 000-7z"
              />
              <path v-else d="M20 14.5A8 8 0 019.5 4 8 8 0 1020 14.5z" />
            </svg>
          </button>
          <span class="text-sm text-nav-muted max-[24rem]:hidden">{{
            session.state.account?.role ?? 'No role'
          }}</span>
          <button type="button" :class="quietButton" @click="signOut">Sign out</button>
        </div>
      </div>

      <nav
        v-if="session.isSignedIn.value"
        aria-label="Main"
        class="flex gap-1 px-3 pb-3 text-sm lg:flex-1 lg:flex-col lg:pb-0"
      >
        <RouterLink
          v-for="link in links"
          :key="link.to"
          :to="link.to"
          class="group flex items-center gap-3 rounded-lg px-3 py-2 font-medium motion-safe:transition-colors"
          :class="
            isCurrent(link.to)
              ? 'bg-nav-active text-on-accent'
              : 'text-nav-muted hover:bg-hover hover:text-nav-ink'
          "
        >
          <svg
            aria-hidden="true"
            viewBox="0 0 24 24"
            class="size-5 shrink-0"
            fill="none"
            stroke="currentColor"
            stroke-width="1.6"
            stroke-linecap="round"
            stroke-linejoin="round"
          >
            <path :d="link.icon" />
          </svg>
          {{ link.label }}
        </RouterLink>
      </nav>

      <div
        v-if="session.isSignedIn.value"
        class="hidden border-t border-nav-line p-4 text-sm lg:block"
      >
        <div class="flex items-center gap-3">
          <span
            aria-hidden="true"
            class="grid size-9 shrink-0 place-items-center rounded-full bg-accent-soft text-sm font-semibold text-accent-strong"
            >{{ initial() }}</span
          >
          <div class="min-w-0">
            <p class="text-xs text-nav-muted">Signed in as</p>
            <p data-testid="role" class="font-medium">
              {{ session.state.account?.role ?? 'No role' }}
            </p>
          </div>
        </div>
        <div class="mt-3 flex gap-2">
          <button type="button" :class="[quietButton, 'flex-1']" @click="signOut">Sign out</button>
          <button
            type="button"
            :class="[quietButton, 'grid size-9 shrink-0 place-items-center px-0']"
            :aria-label="theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'"
            @click="toggle"
          >
            <svg
              aria-hidden="true"
              viewBox="0 0 24 24"
              class="size-4"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
            >
              <path
                v-if="theme === 'dark'"
                d="M12 4v2m0 12v2M4 12h2m12 0h2M6.3 6.3l1.4 1.4m8.6 8.6l1.4 1.4m0-11.4l-1.4 1.4M7.7 16.3l-1.4 1.4M12 8.5a3.5 3.5 0 100 7 3.5 3.5 0 000-7z"
              />
              <path v-else d="M20 14.5A8 8 0 019.5 4 8 8 0 1020 14.5z" />
            </svg>
          </button>
        </div>
      </div>
    </aside>

    <main
      id="main"
      ref="main"
      tabindex="-1"
      class="min-w-0 flex-1 px-4 py-6 outline-none sm:px-8 lg:py-10"
    >
      <div class="mx-auto max-w-6xl">
        <RouterView />
      </div>
    </main>
  </div>
</template>
