<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSession } from '../stores/session'
import BaseButton from '../components/BaseButton.vue'
import FormField from '../components/FormField.vue'
import PageHeader from '../components/PageHeader.vue'
import StateMessage from '../components/StateMessage.vue'
import { inputClass, panelClass } from '../components/styles'

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
  <section :class="[panelClass, 'mx-auto mt-6 max-w-sm p-6 sm:mt-14 sm:p-8']">
    <PageHeader>
      Sign in
      <template #description>Welcome back. Sign in to manage your business.</template>
    </PageHeader>
    <form class="mt-6 space-y-4" novalidate @submit.prevent="submit">
      <FormField id="email" label="E-mail address">
        <template #default="{ aria }">
          <input
            id="email"
            v-model="email"
            type="email"
            autocomplete="username"
            :class="inputClass"
            v-bind="aria"
          />
        </template>
      </FormField>
      <FormField id="password" label="Password">
        <template #default="{ aria }">
          <input
            id="password"
            v-model="password"
            type="password"
            autocomplete="current-password"
            :class="inputClass"
            v-bind="aria"
          />
        </template>
      </FormField>
      <StateMessage v-if="message" kind="error" data-testid="sign-in-error">
        {{ message }}
      </StateMessage>
      <BaseButton type="submit" class="w-full" :disabled="busy">
        {{ busy ? 'Signing in…' : 'Sign in' }}
      </BaseButton>
    </form>
  </section>
</template>
