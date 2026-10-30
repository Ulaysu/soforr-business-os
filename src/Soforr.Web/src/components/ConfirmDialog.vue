<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import BaseButton from './BaseButton.vue'

// A modal confirmation on a native <dialog> (docs/engineering/accessibility.md): focus moves into it when it opens and returns to
// the control that opened it when it goes away. The parent renders it with v-if only while it asks, so it is mounted open.
// Escape and a click on the backdrop mean "cancel". Focus starts on the cancel button, the safe choice.
defineProps<{
  title: string
  confirmLabel: string
  cancelLabel?: string
  busy?: boolean
  tone?: 'danger' | 'primary'
}>()
const emit = defineEmits<{ confirm: []; cancel: [] }>()

const dialog = ref<HTMLDialogElement | null>(null)
const cancelButton = ref<InstanceType<typeof BaseButton> | null>(null)
let opener: Element | null = null

onMounted(() => {
  opener = document.activeElement
  const element = dialog.value
  if (!element) return
  // jsdom (the unit tests) has no showModal; a real browser always does.
  if (typeof element.showModal === 'function') element.showModal()
  else element.setAttribute('open', '')
  ;(cancelButton.value?.$el as HTMLElement | undefined)?.focus()
})

onBeforeUnmount(() => {
  // Close first: while a modal dialog is open everything behind it is inert and cannot take focus.
  dialog.value?.close?.()
  if (opener instanceof HTMLElement && opener.isConnected) opener.focus()
})

function onBackdropClick(event: MouseEvent) {
  if (event.target === dialog.value) emit('cancel')
}
</script>

<template>
  <dialog
    ref="dialog"
    aria-labelledby="confirm-title"
    class="m-auto w-[calc(100%-2rem)] max-w-md rounded-xl border border-line bg-raised p-0 text-ink shadow-pop"
    @cancel.prevent="emit('cancel')"
    @click="onBackdropClick"
  >
    <div class="p-6">
      <h2 id="confirm-title" class="text-lg font-semibold">{{ title }}</h2>
      <div class="mt-2 text-sm text-muted"><slot /></div>
    </div>
    <div
      class="flex flex-wrap justify-end gap-3 rounded-b-xl border-t border-line bg-sunken px-6 py-4"
    >
      <BaseButton ref="cancelButton" variant="secondary" @click="emit('cancel')">
        {{ cancelLabel ?? 'Cancel' }}
      </BaseButton>
      <BaseButton :variant="tone ?? 'primary'" :disabled="busy" @click="emit('confirm')">
        {{ confirmLabel }}
      </BaseButton>
    </div>
  </dialog>
</template>
