<script setup lang="ts">
import { RouterLink, RouterView, useRouter } from 'vue-router'
import { useSession } from './stores/session'

const session = useSession()
const router = useRouter()

async function signOut() {
  await session.signOut()
  await router.push({ name: 'sign-in' })
}
</script>

<template>
  <div class="min-h-screen bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
    <header
      class="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-800"
    >
      <div class="flex items-center gap-6">
        <span class="text-lg font-semibold">Soforr Business OS</span>
        <nav v-if="session.isSignedIn.value" aria-label="Main" class="flex gap-4 text-sm">
          <RouterLink class="underline-offset-4 hover:underline" to="/products"
            >Products</RouterLink
          >
        </nav>
      </div>
      <div v-if="session.isSignedIn.value" class="flex items-center gap-4 text-sm">
        <span data-testid="role" class="text-slate-600 dark:text-slate-400">{{
          session.state.account?.role ?? 'No role'
        }}</span>
        <button
          type="button"
          class="rounded border border-slate-300 px-3 py-1 hover:bg-slate-100 dark:border-slate-700 dark:hover:bg-slate-800"
          @click="signOut"
        >
          Sign out
        </button>
      </div>
    </header>
    <main class="mx-auto max-w-5xl px-6 py-8">
      <RouterView />
    </main>
  </div>
</template>
