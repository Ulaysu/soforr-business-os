<script setup lang="ts">
import BaseButton from './BaseButton.vue'

defineProps<{ page: number; pageCount: number; summary?: string }>()
defineEmits<{ go: [page: number] }>()
</script>

<template>
  <nav
    v-if="pageCount > 1"
    aria-label="Pages"
    class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 text-sm"
  >
    <span v-if="summary" class="text-muted" data-testid="page-summary">{{ summary }}</span>
    <div class="ml-auto flex items-center gap-2">
      <BaseButton variant="secondary" :disabled="page <= 1" @click="$emit('go', page - 1)">
        Previous
      </BaseButton>
      <span data-testid="page-info" class="px-2 text-muted tabular-nums"
        >Page {{ page }} of {{ pageCount }}</span
      >
      <BaseButton variant="secondary" :disabled="page >= pageCount" @click="$emit('go', page + 1)">
        Next
      </BaseButton>
    </div>
  </nav>
</template>
