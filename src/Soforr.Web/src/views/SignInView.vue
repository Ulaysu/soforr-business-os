<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSession } from '../stores/session'

const session = useSession()
const route = useRoute()
const router = useRouter()

const email = ref('')
const password = ref('')
const busy = ref(false)
const message = ref<string | null>(null)

async function submit() {
  message.value = null
  // The server checks everything; this only avoids sending an empty form.
  if (email.value.trim() === '' || password.value === '') {
    message.value = 'Enter your e-mail address and password.'
    return
  }

  busy.value = true
  const problem = await session.signIn(email.value.trim(), password.value)
  busy.value = false

  if (problem) {
    // The server answers the same way for an unknown address and a wrong password, so the screen does too.
    message.value =
      problem.status === 401 ? 'The e-mail address or password is not right.' : problem.message
    password.value = ''
    return
  }

  const target = typeof route.query.redirect === 'string' ? route.query.redirect : null
  await router.push(
    target && target.startsWith('/') && !target.startsWith('//') ? target : { name: 'products' },
  )
}
</script>

<template>
  <section class="mx-auto max-w-sm">
    <h1 class="text-2xl font-semibold">Sign in</h1>
    <form class="mt-6 space-y-4" novalidate @submit.prevent="submit">
      <div>
        <label class="block text-sm font-medium" for="email">E-mail address</label>
        <input
          id="email"
          v-model="email"
          type="email"
          autocomplete="username"
          class="mt-1 w-full rounded border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700"
        />
      </div>
      <div>
        <label class="block text-sm font-medium" for="password">Password</label>
        <input
          id="password"
          v-model="password"
          type="password"
          autocomplete="current-password"
          class="mt-1 w-full rounded border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700"
        />
      </div>
      <p
        v-if="message"
        role="alert"
        data-testid="sign-in-error"
        class="text-sm text-red-600 dark:text-red-400"
      >
        {{ message }}
      </p>
      <button
        type="submit"
        :disabled="busy"
        class="w-full rounded bg-blue-600 px-4 py-2 font-medium text-white hover:bg-blue-700 disabled:opacity-60"
      >
        {{ busy ? 'Signing in…' : 'Sign in' }}
      </button>
    </form>
  </section>
</template>
