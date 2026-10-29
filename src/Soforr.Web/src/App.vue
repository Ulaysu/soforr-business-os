<script setup lang="ts">
import { RouterLink, RouterView, useRouter } from 'vue-router'
import BaseButton from './components/BaseButton.vue'
import { useSession } from './stores/session'

const session = useSession()
const router = useRouter()

const links = [
  { to: '/products', label: 'Products' },
  { to: '/stock', label: 'Stock' },
]

async function signOut() {
  await session.signOut()
  await router.push({ name: 'sign-in' })
}
</script>

<template>
  <div class="min-h-screen bg-surface text-ink">
    <header class="flex items-center justify-between border-b border-line bg-raised px-6 py-3">
      <div class="flex items-center gap-6">
        <span class="text-lg font-semibold">Soforr Business OS</span>
        <nav v-if="session.isSignedIn.value" aria-label="Main" class="flex gap-4 text-sm">
          <RouterLink
            v-for="link in links"
            :key="link.to"
            :to="link.to"
            class="underline-offset-4 hover:underline aria-[current=page]:font-semibold aria-[current=page]:text-accent"
            >{{ link.label }}</RouterLink
          >
        </nav>
      </div>
      <div v-if="session.isSignedIn.value" class="flex items-center gap-4 text-sm">
        <span data-testid="role" class="text-muted">{{
          session.state.account?.role ?? 'No role'
        }}</span>
        <BaseButton variant="secondary" @click="signOut">Sign out</BaseButton>
      </div>
    </header>
    <main class="mx-auto max-w-7xl px-6 py-8">
      <RouterView />
    </main>
  </div>
</template>
