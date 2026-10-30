<script setup lang="ts">
// A dense, scrollable table inside a panel. Screens write plain <th scope="col"> and <td>; the spacing, borders, header look and
// row hover come from here. The footer slot (paging) sits under the table, inside the panel.
defineProps<{ caption?: string }>()
</script>

<template>
  <div class="overflow-hidden rounded-lg border border-line bg-raised shadow-panel">
    <div class="overflow-x-auto">
      <table
        class="w-full text-left text-sm [&_td]:px-3 [&_td]:py-3 [&_th]:px-3 [&_th]:py-2.5 [&_th]:text-xs [&_th]:font-medium [&_th]:tracking-wide [&_th]:whitespace-nowrap [&_th]:text-muted [&_th]:uppercase [&_thead]:border-b [&_thead]:border-line [&_thead]:bg-sunken [&_tbody_tr]:border-b [&_tbody_tr]:border-line motion-safe:[&_tbody_tr]:transition-colors [&_tbody_tr:hover]:bg-hover [&_tbody_tr:last-child]:border-b-0"
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
    <div v-if="$slots.footer" class="border-t border-line bg-sunken px-4 py-2.5">
      <slot name="footer" />
    </div>
  </div>
</template>
