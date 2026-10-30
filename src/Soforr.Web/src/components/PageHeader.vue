<template>
  <div class="flex flex-wrap items-end justify-between gap-x-6 gap-y-3">
    <div class="min-w-0">
      <h1 tabindex="-1" class="text-2xl font-semibold text-ink outline-none"><slot /></h1>
      <p v-if="$slots.description" class="mt-1 text-sm text-muted"><slot name="description" /></p>
    </div>
    <div v-if="$slots.actions" class="flex items-center gap-3"><slot name="actions" /></div>
  </div>
</template>
