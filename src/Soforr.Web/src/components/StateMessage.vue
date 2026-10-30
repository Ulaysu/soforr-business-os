<script setup lang="ts">
// The one place that words a loading, empty, forbidden or failed state. Errors are announced (role="alert").
defineProps<{ kind: 'loading' | 'empty' | 'forbidden' | 'error' }>()
</script>

<template>
  <p
    :role="kind === 'error' ? 'alert' : kind === 'loading' ? 'status' : undefined"
    :class="[
      'text-sm',
      kind === 'error'
        ? 'rounded-md border border-danger/40 bg-danger-soft px-4 py-3 text-danger'
        : 'text-muted',
    ]"
  >
    <slot />
  </p>
</template>
