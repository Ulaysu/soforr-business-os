<script setup lang="ts">
import { buttonClass, type ButtonVariant } from './styles'

withDefaults(defineProps<{ variant?: ButtonVariant; type?: 'button' | 'submit' }>(), {
  variant: 'primary',
  type: 'button',
})
</script>

<template>
  <button :type="type" :class="buttonClass(variant)"><slot /></button>
</template>
