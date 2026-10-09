<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import type { components } from '../api/schema'
import { formatMoney, formatNumber } from '../format'
import { canManageProducts, useSession } from '../stores/session'

type Product = components['schemas']['ProductDto']

const pageSize = 20
const api = useApi()
const session = useSession()
const canManage = computed(() => canManageProducts(session.state.account?.role))

const search = ref('')
const includeInactive = ref(false)
const page = ref(1)
const items = ref<Product[]>([])
const totalCount = ref(0)
const loading = ref(false)
const error = ref<string | null>(null)
const confirming = ref<string | null>(null)
const deactivating = ref<string | null>(null)

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))

async function load() {
  loading.value = true
  error.value = null
  try {
    const {
      data,
      error: failure,
      response,
    } = await api.GET('/api/v1/products', {
      params: {
        query: {
          q: search.value.trim() || undefined,
          includeInactive: includeInactive.value,
          page: page.value,
          pageSize,
        },
      },
    })
    if (response.ok && data) {
      items.value = data.items
      totalCount.value = Number(data.totalCount)
    } else {
      error.value = toProblem(failure, response.status).message
    }
  } catch {
    error.value = networkProblem().message
  } finally {
    loading.value = false
  }
}

// Typing in the search box waits a moment before asking the server, and a new search starts at the first page.
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, () => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    page.value = 1
    void load()
  }, 300)
})
watch(includeInactive, () => {
  page.value = 1
  void load()
})
onBeforeUnmount(() => clearTimeout(timer))
onMounted(load)

function goTo(next: number) {
  page.value = next
  void load()
}

async function deactivate(product: Product) {
  deactivating.value = product.id
  error.value = null
  try {
    const { error: failure, response } = await api.POST('/api/v1/products/{id}/deactivate', {
      params: { path: { id: product.id } },
    })
    if (!response.ok) {
      error.value = toProblem(failure, response.status).message
    }
  } catch {
    error.value = networkProblem().message
  } finally {
    deactivating.value = null
    confirming.value = null
  }
  await load()
}
</script>

<template>
  <section>
    <div class="flex flex-wrap items-center justify-between gap-3">
      <h1 class="text-2xl font-semibold">Products</h1>
      <RouterLink
        v-if="canManage"
        to="/products/new"
        class="rounded bg-blue-600 px-4 py-2 font-medium text-white hover:bg-blue-700"
      >
        New product
      </RouterLink>
    </div>

    <div class="mt-4 flex flex-wrap items-center gap-4">
      <label class="sr-only" for="search">Search products</label>
      <input
        id="search"
        v-model="search"
        type="search"
        placeholder="Search by SKU or name"
        class="w-full max-w-xs rounded border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700"
      />
      <label class="flex items-center gap-2 text-sm">
        <input id="includeInactive" v-model="includeInactive" type="checkbox" />
        Show inactive
      </label>
    </div>

    <p
      v-if="error"
      role="alert"
      data-testid="products-error"
      class="mt-4 text-sm text-red-600 dark:text-red-400"
    >
      {{ error }}
    </p>

    <p v-if="loading && items.length === 0" class="mt-6 text-sm text-slate-500">Loading…</p>
    <p
      v-else-if="!loading && !error && items.length === 0"
      data-testid="products-empty"
      class="mt-6 text-sm text-slate-500"
    >
      {{ search.trim() ? 'No products match your search.' : 'There are no products yet.' }}
    </p>

    <div v-if="items.length > 0" class="mt-6 overflow-x-auto">
      <table class="w-full text-left text-sm">
        <thead class="border-b border-slate-200 text-slate-500 dark:border-slate-800">
          <tr>
            <th class="py-2 pr-4 font-medium">SKU</th>
            <th class="py-2 pr-4 font-medium">Name</th>
            <th class="py-2 pr-4 font-medium">Unit</th>
            <th class="py-2 pr-4 text-right font-medium">Price</th>
            <th class="py-2 pr-4 text-right font-medium">Cost</th>
            <th class="py-2 pr-4 text-right font-medium">Reorder at</th>
            <th class="py-2 pr-4 font-medium">Status</th>
            <th v-if="canManage" class="py-2 font-medium"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="product in items"
            :key="product.id"
            data-testid="product-row"
            class="border-b border-slate-100 dark:border-slate-900"
          >
            <td class="py-2 pr-4 font-mono">{{ product.sku }}</td>
            <td class="py-2 pr-4">{{ product.name }}</td>
            <td class="py-2 pr-4">{{ product.unitOfMeasure }}</td>
            <td class="py-2 pr-4 text-right">
              {{ formatMoney(product.sellingPrice, product.currency) }}
            </td>
            <td class="py-2 pr-4 text-right">
              {{ formatMoney(product.costPrice, product.currency) }}
            </td>
            <td class="py-2 pr-4 text-right">{{ formatNumber(product.reorderPoint) }}</td>
            <td class="py-2 pr-4">{{ product.isActive ? 'Active' : 'Inactive' }}</td>
            <td v-if="canManage" class="py-2 text-right whitespace-nowrap">
              <template v-if="confirming === product.id">
                <span class="mr-2">Deactivate {{ product.name }}?</span>
                <button
                  type="button"
                  class="mr-2 text-red-600 underline dark:text-red-400"
                  :disabled="deactivating === product.id"
                  @click="deactivate(product)"
                >
                  Yes, deactivate
                </button>
                <button type="button" class="underline" @click="confirming = null">Cancel</button>
              </template>
              <template v-else>
                <RouterLink
                  v-if="product.isActive"
                  :to="`/products/${product.id}/edit`"
                  class="mr-3 text-blue-600 underline dark:text-blue-400"
                >
                  Edit
                </RouterLink>
                <button
                  v-if="product.isActive"
                  type="button"
                  class="underline"
                  :aria-label="`Deactivate ${product.name}`"
                  @click="confirming = product.id"
                >
                  Deactivate
                </button>
              </template>
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
