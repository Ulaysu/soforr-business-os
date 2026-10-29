<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import DataTable from '../components/DataTable.vue'
import PageHeader from '../components/PageHeader.vue'
import ListPager from '../components/ListPager.vue'
import StateMessage from '../components/StateMessage.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { buttonClass, inputClass } from '../components/styles'
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
let loadGeneration = 0

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))

function asNumber(value: number | string | null | undefined): number | null {
  return value === null || value === undefined ? null : Number(value)
}

async function load() {
  const current = ++loadGeneration
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
      if (current !== loadGeneration) return
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
      if (current !== loadGeneration) return
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
    if (current === loadGeneration) error.value = networkProblem().message
  } finally {
    if (current === loadGeneration) loading.value = false
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
    <PageHeader>Stock</PageHeader>

    <div class="mt-4 flex flex-wrap items-center gap-4">
      <label class="sr-only" for="search">Search products</label>
      <input
        id="search"
        v-model="search"
        type="search"
        placeholder="Search by SKU or name"
        :disabled="lowOnly"
        :class="[inputClass, 'mt-0 max-w-xs']"
      />
      <label class="flex items-center gap-2 text-sm">
        <input id="lowOnly" v-model="lowOnly" type="checkbox" class="accent-accent" />
        Show only low stock
      </label>
    </div>

    <StateMessage v-if="error" kind="error" data-testid="stock-error" class="mt-4">
      {{ error }}
    </StateMessage>

    <StateMessage v-if="loading && rows.length === 0" kind="loading" class="mt-6">
      Loading…
    </StateMessage>
    <StateMessage
      v-else-if="!loading && !error && rows.length === 0"
      kind="empty"
      data-testid="stock-empty"
      class="mt-6"
    >
      {{
        lowOnly
          ? 'Nothing is low on stock.'
          : search.trim()
            ? 'No products match your search.'
            : 'There are no products yet.'
      }}
    </StateMessage>

    <div v-if="rows.length > 0" class="mt-6">
      <DataTable caption="Stock levels">
        <template #head>
          <tr>
            <th scope="col">SKU</th>
            <th scope="col">Name</th>
            <th scope="col" class="text-right">On hand</th>
            <th scope="col">Unit</th>
            <th scope="col" class="text-right">Reorder at</th>
            <th scope="col">Status</th>
            <th scope="col"><span class="sr-only">Details</span></th>
          </tr>
        </template>
        <tr v-for="row in rows" :key="row.productId" data-testid="stock-row">
          <td class="font-mono">{{ row.sku }}</td>
          <td>{{ row.name }}</td>
          <td class="text-right" data-testid="quantity">{{ formatNumber(row.quantity) }}</td>
          <td>{{ row.unitOfMeasure }}</td>
          <td class="text-right">{{ formatNumber(row.reorderPoint) }}</td>
          <td>
            <StatusBadge :tone="row.isLow ? 'warn' : 'ok'">{{
              row.isLow ? 'Low' : 'OK'
            }}</StatusBadge>
          </td>
          <td class="text-right">
            <RouterLink
              :to="`/stock/${row.productId}`"
              :class="buttonClass('link')"
              :aria-label="`Details of ${row.name}`"
              >Details</RouterLink
            >
          </td>
        </tr>
      </DataTable>

      <ListPager :page="page" :page-count="pageCount" @go="goTo" />
    </div>
  </section>
</template>
