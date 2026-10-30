<script setup lang="ts">
export type BadgeTone = 'ok' | 'warn' | 'danger' | 'info' | 'neutral'

// The badge always carries words; the color only reinforces them (docs/engineering/accessibility.md).
const tones: Record<BadgeTone, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  danger: 'bg-danger-soft text-danger',
  info: 'bg-info-soft text-info',
  neutral: 'bg-sunken text-muted ring-1 ring-line ring-inset',
}

withDefaults(defineProps<{ tone?: BadgeTone }>(), { tone: 'neutral' })
</script>

<template>
  <span
    :class="[
      'inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-xs font-medium',
      tones[tone],
    ]"
  >
    <span aria-hidden="true" class="size-1.5 rounded-full bg-current"></span>
    <slot />
  </span>
</template>
