<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  id: string
  label: string
  error?: string | null
  errorTestId?: string
  hint?: string
  required?: boolean
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
  'aria-required': props.required ? true : undefined,
}))
</script>

<template>
  <div>
    <label class="block text-sm font-medium text-ink" :for="id"
      >{{ label
      }}<span v-if="required" aria-hidden="true" class="ml-0.5 text-danger">*</span></label
    >
    <slot :aria="aria" />
    <p v-if="hint" :id="hintId" class="mt-1.5 text-xs text-muted">{{ hint }}</p>
    <p v-if="error" :id="errorId" :data-testid="errorTestId" class="mt-1.5 text-sm text-danger">
      {{ error }}
    </p>
  </div>
</template>
