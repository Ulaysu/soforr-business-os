<script setup lang="ts">
export type BadgeTone = 'ok' | 'warn' | 'danger' | 'info' | 'neutral'

// The badge always carries words; the color only reinforces them (docs/engineering/accessibility.md).
const tones: Record<BadgeTone, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  danger: 'bg-danger-soft text-danger',
  info: 'bg-info-soft text-info',
  neutral: 'bg-line text-ink',
}

withDefaults(defineProps<{ tone?: BadgeTone }>(), { tone: 'neutral' })
</script>

<template>
  <span :class="['inline-block rounded-md px-2 py-0.5 text-xs font-medium', tones[tone]]">
    <slot />
  </span>
</template>
