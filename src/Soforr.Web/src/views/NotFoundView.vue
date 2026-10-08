<script setup lang="ts">
import { RouterLink } from 'vue-router'
</script>

<template>
  <section>
    <h1 class="text-2xl font-semibold">Page not found</h1>
    <RouterLink class="mt-4 inline-block text-blue-600 underline dark:text-blue-400" to="/">
      Back to the start
    </RouterLink>
  </section>
</template>
