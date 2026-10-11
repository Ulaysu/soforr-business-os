<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import { formatNumber } from '../format'

interface Row {
  productId: string
  sku: string
  name: string
  unitOfMeasure: string
  quantity: number
  reorderPoint: number | null
  isLow: boolean
}

const pageSize = 20
const api = useApi()

const search = ref('')
const lowOnly = ref(false)
const page = ref(1)
const rows = ref<Row[]>([])
const totalCount = ref(0)
const loading = ref(false)
const error = ref<string | null>(null)

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))

function asNumber(value: number | string | null | undefined): number | null {
  return value === null || value === undefined ? null : Number(value)
}

async function load() {
  loading.value = true
  error.value = null
  try {
    if (lowOnly.value) {
      // The products at or below their reorder point (the server's one definition of "low").
      const {
        data,
        error: failure,
        response,
      } = await api.GET('/api/v1/inventory/low-stock', {
        params: { query: { page: page.value, pageSize } },
      })
      if (response.ok && data) {
        rows.value = data.items.map((item) => ({
          productId: item.productId,
          sku: item.sku,
          name: item.name,
          unitOfMeasure: item.unitOfMeasure,
          quantity: Number(item.quantity),
          reorderPoint: asNumber(item.reorderPoint),
          isLow: true,
        }))
        totalCount.value = Number(data.totalCount)
      } else {
        error.value = toProblem(failure, response.status).message
      }
    } else {
      const {
        data,
        error: failure,
        response,
      } = await api.GET('/api/v1/inventory/stock', {
        params: { query: { q: search.value.trim() || undefined, page: page.value, pageSize } },
      })
      if (response.ok && data) {
        rows.value = data.items.map((item) => ({
          productId: item.productId,
          sku: item.sku,
          name: item.name,
          unitOfMeasure: item.unitOfMeasure,
          quantity: Number(item.quantity),
          reorderPoint: asNumber(item.reorderPoint),
          isLow: item.isLow,
        }))
        totalCount.value = Number(data.totalCount)
      } else {
        error.value = toProblem(failure, response.status).message
      }
    }
  } catch {
    error.value = networkProblem().message
  } finally {
    loading.value = false
  }
}

let timer: ReturnType<typeof setTimeout> | undefined
watch(search, () => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    page.value = 1
    void load()
  }, 300)
})
watch(lowOnly, () => {
  page.value = 1
  void load()
})
onBeforeUnmount(() => clearTimeout(timer))
onMounted(load)

function goTo(next: number) {
  page.value = next
  void load()
}
</script>

<template>
  <section>
    <h1 class="text-2xl font-semibold">Stock</h1>

    <div class="mt-4 flex flex-wrap items-center gap-4">
      <label class="sr-only" for="search">Search products</label>
      <input
        id="search"
        v-model="search"
        type="search"
        placeholder="Search by SKU or name"
        :disabled="lowOnly"
        class="w-full max-w-xs rounded border border-slate-300 bg-transparent px-3 py-2 disabled:opacity-50 dark:border-slate-700"
      />
      <label class="flex items-center gap-2 text-sm">
        <input id="lowOnly" v-model="lowOnly" type="checkbox" />
        Show only low stock
      </label>
    </div>

    <p
      v-if="error"
      role="alert"
      data-testid="stock-error"
      class="mt-4 text-sm text-red-600 dark:text-red-400"
    >
      {{ error }}
    </p>

    <p v-if="loading && rows.length === 0" class="mt-6 text-sm text-slate-500">Loading…</p>
    <p
      v-else-if="!loading && !error && rows.length === 0"
      data-testid="stock-empty"
      class="mt-6 text-sm text-slate-500"
    >
      {{
        lowOnly
          ? 'Nothing is low on stock.'
          : search.trim()
            ? 'No products match your search.'
            : 'There are no products yet.'
      }}
    </p>

    <div v-if="rows.length > 0" class="mt-6 overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead class="border-b border-slate-200 text-slate-500 dark:border-slate-800">
          <tr>
            <th class="py-2 pr-4 font-medium">SKU</th>
            <th class="py-2 pr-4 font-medium">Name</th>
            <th class="py-2 pr-4 text-right font-medium">On hand</th>
            <th class="py-2 pr-4 font-medium">Unit</th>
            <th class="py-2 pr-4 text-right font-medium">Reorder at</th>
            <th class="py-2 pr-4 font-medium">Status</th>
            <th class="py-2 font-medium"><span class="sr-only">Details</span></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="row in rows"
            :key="row.productId"
            data-testid="stock-row"
            class="border-b border-slate-100 dark:border-slate-900"
          >
            <td class="py-2 pr-4 font-mono">{{ row.sku }}</td>
            <td class="py-2 pr-4">{{ row.name }}</td>
            <td class="py-2 pr-4 text-right" data-testid="quantity">
              {{ formatNumber(row.quantity) }}
            </td>
            <td class="py-2 pr-4">{{ row.unitOfMeasure }}</td>
            <td class="py-2 pr-4 text-right">{{ formatNumber(row.reorderPoint) }}</td>
            <td class="py-2 pr-4">
              <span
                v-if="row.isLow"
                class="rounded bg-amber-100 px-2 py-0.5 text-amber-900 dark:bg-amber-900 dark:text-amber-100"
                >Low</span
              >
              <span v-else class="text-slate-500">OK</span>
            </td>
            <td class="py-2 text-right">
              <RouterLink
                :to="`/stock/${row.productId}`"
                class="text-blue-600 underline dark:text-blue-400"
                :aria-label="`Details of ${row.name}`"
                >Details</RouterLink
              >
            </td>
          </tr>
        </tbody>
      </table>

      <nav v-if="pageCount > 1" aria-label="Pages" class="mt-4 flex items-center gap-4 text-sm">
        <button
          type="button"
          class="underline disabled:no-underline disabled:opacity-50"
          :disabled="page <= 1"
          @click="goTo(page - 1)"
        >
          Previous
        </button>
        <span data-testid="page-info">Page {{ page }} of {{ pageCount }}</span>
        <button
          type="button"
          class="underline disabled:no-underline disabled:opacity-50"
          :disabled="page >= pageCount"
          @click="goTo(page + 1)"
        >
          Next
        </button>
      </nav>
    </div>
  </section>
</template>
