<script setup lang="ts">
// A dense, scrollable table. Screens write plain <th scope="col"> and <td>; the spacing, borders and header look come from here.
defineProps<{ caption?: string }>()
</script>

<template>
  <div class="overflow-x-auto rounded-md border border-line bg-raised">
    <table
      class="w-full text-left text-sm [&_td]:px-3 [&_td]:py-2 [&_th]:px-3 [&_th]:py-2 [&_th]:font-medium [&_th]:text-muted [&_thead]:border-b [&_thead]:border-line [&_tbody_tr]:border-b [&_tbody_tr]:border-line [&_tbody_tr:last-child]:border-b-0"
    >
      <caption v-if="caption" class="sr-only">
        {{
          caption
        }}
      </caption>
      <thead>
        <slot name="head" />
      </thead>
      <tbody>
        <slot />
      </tbody>
    </table>
  </div>
</template>
