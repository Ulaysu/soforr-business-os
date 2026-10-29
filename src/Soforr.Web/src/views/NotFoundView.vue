<script setup lang="ts">
import { RouterLink } from 'vue-router'
import PageHeader from '../components/PageHeader.vue'
import { buttonClass } from '../components/styles'
</script>

<template>
  <section>
    <PageHeader>Page not found</PageHeader>
    <RouterLink :class="[buttonClass('link'), 'mt-4']" to="/">Back to the start</RouterLink>
  </section>
</template>
