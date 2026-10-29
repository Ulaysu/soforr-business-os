<template>
  <div class="flex flex-wrap items-center justify-between gap-3">
    <h1 class="text-2xl font-semibold"><slot /></h1>
    <div v-if="$slots.actions" class="flex items-center gap-3"><slot name="actions" /></div>
  </div>
</template>
