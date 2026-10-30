<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink, RouterView, useRouter } from 'vue-router'
import BaseButton from './components/BaseButton.vue'
import { useSession } from './stores/session'

const session = useSession()
const router = useRouter()

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

const main = ref<HTMLElement | null>(null)

async function signOut() {
  await session.signOut()
  await router.push({ name: 'sign-in' })
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
        'border-b border-line bg-raised',
        session.isSignedIn.value &&
          'lg:sticky lg:top-0 lg:flex lg:h-screen lg:w-64 lg:shrink-0 lg:flex-col lg:border-r lg:border-b-0',
      ]"
    >
      <div class="flex items-center justify-between gap-3 px-4 py-3 sm:px-6 lg:px-5 lg:py-5">
        <div class="flex items-center gap-3">
          <span
            aria-hidden="true"
            class="grid size-8 place-items-center rounded-lg bg-accent text-sm font-bold text-on-accent"
            >S</span
          >
          <span class="leading-tight">
            <span class="block text-sm font-semibold whitespace-nowrap">Soforr</span>
            <span class="block text-xs whitespace-nowrap text-muted">Business OS</span>
          </span>
        </div>
        <div v-if="session.isSignedIn.value" class="flex items-center gap-3 text-sm lg:hidden">
          <span data-testid="role-compact" class="text-muted">{{
            session.state.account?.role ?? 'No role'
          }}</span>
          <BaseButton variant="secondary" @click="signOut">Sign out</BaseButton>
        </div>
      </div>

      <nav
        v-if="session.isSignedIn.value"
        aria-label="Main"
        class="flex gap-1 px-3 pb-2 text-sm lg:flex-1 lg:flex-col lg:pb-0"
      >
        <RouterLink
          v-for="link in links"
          :key="link.to"
          :to="link.to"
          class="flex items-center gap-3 rounded-md px-3 py-2 font-medium text-muted motion-safe:transition-colors hover:bg-hover hover:text-ink aria-[current=page]:bg-accent-soft aria-[current=page]:text-accent-strong"
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

      <div v-if="session.isSignedIn.value" class="hidden border-t border-line p-4 text-sm lg:block">
        <p class="text-xs text-muted">Signed in as</p>
        <p data-testid="role" class="mt-0.5 font-medium">
          {{ session.state.account?.role ?? 'No role' }}
        </p>
        <BaseButton variant="secondary" class="mt-3 w-full" @click="signOut">Sign out</BaseButton>
      </div>
    </aside>

    <main
      id="main"
      ref="main"
      tabindex="-1"
      class="min-w-0 flex-1 outline-none px-4 py-6 sm:px-8 lg:py-10"
    >
      <div class="mx-auto max-w-6xl">
        <RouterView />
      </div>
    </main>
  </div>
</template>
