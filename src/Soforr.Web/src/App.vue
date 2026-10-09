<script setup lang="ts">
import { RouterView } from 'vue-router'
</script>

<template>
  <div class="min-h-screen bg-white text-slate-900 dark:bg-slate-950 dark:text-slate-100">
    <header class="border-b border-slate-200 px-6 py-4 dark:border-slate-800">
      <span class="text-lg font-semibold">Soforr Business OS</span>
    </header>
    <main class="mx-auto max-w-3xl px-6 py-10">
      <RouterView />
    </main>
  </div>
</template>
