<script setup lang="ts">
import { onMounted, ref } from 'vue'

type ApiState = 'checking' | 'reachable' | 'unreachable'

const api = ref<ApiState>('checking')

// A cheap, unauthenticated check that the API behind the proxy answers. It is the same probe the offline work will
// use later to tell "the browser says online" from "the API is reachable" (ADR 0007).
onMounted(async () => {
  try {
    const response = await fetch('/health/live')
    api.value = response.ok ? 'reachable' : 'unreachable'
  } catch {
    api.value = 'unreachable'
  }
})
</script>

<template>
  <section>
    <h1 class="text-2xl font-semibold">Welcome</h1>
    <p class="mt-2 text-slate-600 dark:text-slate-400">
      This is the application shell. Business screens arrive with their own work items.
    </p>
    <p class="mt-6 text-sm" data-testid="api-status">
      <span v-if="api === 'checking'">Checking the server…</span>
      <span v-else-if="api === 'reachable'">Server reachable</span>
      <span v-else>Server not reachable</span>
    </p>
  </section>
</template>
