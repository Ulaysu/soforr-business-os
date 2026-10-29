<script setup lang="ts">
export interface Fact {
  label: string
  value: string
  testId?: string
  emphasis?: boolean
}

// Label and value pairs ("On hand: 12"): the shape of a record's key facts. The values are shown as the server sent them.
defineProps<{ facts: Fact[] }>()
</script>

<template>
  <dl class="grid grid-cols-2 gap-y-2 text-sm">
    <template v-for="fact in facts" :key="fact.label">
      <dt class="text-muted">{{ fact.label }}</dt>
      <dd
        :data-testid="fact.testId"
        :class="['text-right', fact.emphasis && 'text-lg font-semibold']"
      >
        {{ fact.value }}
      </dd>
    </template>
  </dl>
</template>
