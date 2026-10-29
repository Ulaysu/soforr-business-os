<script setup lang="ts">
import BaseButton from './BaseButton.vue'

defineProps<{ page: number; pageCount: number }>()
defineEmits<{ go: [page: number] }>()
</script>

<template>
  <nav v-if="pageCount > 1" aria-label="Pages" class="mt-4 flex items-center gap-4 text-sm">
    <BaseButton variant="link" :disabled="page <= 1" @click="$emit('go', page - 1)">
      Previous
    </BaseButton>
    <span data-testid="page-info">Page {{ page }} of {{ pageCount }}</span>
    <BaseButton variant="link" :disabled="page >= pageCount" @click="$emit('go', page + 1)">
      Next
    </BaseButton>
  </nav>
</template>
