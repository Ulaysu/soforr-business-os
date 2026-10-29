<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  id: string
  label: string
  error?: string | null
  errorTestId?: string
  hint?: string
}>()

const errorId = computed(() => `${props.id}-error`)
const hintId = computed(() => `${props.id}-hint`)
// The control inside binds these (v-bind="aria") so a screen reader reads the hint and the error with the label.
const aria = computed(() => ({
  'aria-describedby':
    [props.hint ? hintId.value : null, props.error ? errorId.value : null]
      .filter(Boolean)
      .join(' ') || undefined,
  'aria-invalid': props.error ? true : undefined,
}))
</script>

<template>
  <div>
    <label class="block text-sm font-medium" :for="id">{{ label }}</label>
    <slot :aria="aria" />
    <p v-if="hint" :id="hintId" class="mt-1 text-xs text-muted">{{ hint }}</p>
    <p v-if="error" :id="errorId" :data-testid="errorTestId" class="mt-1 text-sm text-danger">
      {{ error }}
    </p>
  </div>
</template>
