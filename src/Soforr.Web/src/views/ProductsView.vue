<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import type { components } from '../api/schema'
import BaseButton from '../components/BaseButton.vue'
import DataTable from '../components/DataTable.vue'
import PageHeader from '../components/PageHeader.vue'
import ListPager from '../components/ListPager.vue'
import StateMessage from '../components/StateMessage.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { buttonClass, inputClass } from '../components/styles'
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
let loadGeneration = 0

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))

async function load() {
  const current = ++loadGeneration
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
    if (current !== loadGeneration) return
    if (response.ok && data) {
      items.value = data.items
      totalCount.value = Number(data.totalCount)
    } else {
      error.value = toProblem(failure, response.status).message
    }
  } catch {
    if (current === loadGeneration) error.value = networkProblem().message
  } finally {
    if (current === loadGeneration) loading.value = false
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
  let succeeded = false
  try {
    const { error: failure, response } = await api.POST('/api/v1/products/{id}/deactivate', {
      params: { path: { id: product.id } },
    })
    if (!response.ok) {
      error.value = toProblem(failure, response.status).message
    } else {
      succeeded = true
    }
  } catch {
    error.value = networkProblem().message
  } finally {
    deactivating.value = null
    confirming.value = null
  }
  // Do not reload on failure: load() clears the error and would hide why the action failed.
  if (succeeded) await load()
}
</script>

<template>
  <section>
    <PageHeader>
      Products
      <template #actions>
        <RouterLink v-if="canManage" to="/products/new" :class="buttonClass('primary')">
          New product
        </RouterLink>
      </template>
    </PageHeader>

    <div class="mt-4 flex flex-wrap items-center gap-4">
      <label class="sr-only" for="search">Search products</label>
      <input
        id="search"
        v-model="search"
        type="search"
        placeholder="Search by SKU or name"
        :class="[inputClass, 'mt-0 max-w-xs']"
      />
      <label class="flex items-center gap-2 text-sm">
        <input
          id="includeInactive"
          v-model="includeInactive"
          type="checkbox"
          class="accent-accent"
        />
        Show inactive
      </label>
    </div>

    <StateMessage v-if="error" kind="error" data-testid="products-error" class="mt-4">
      {{ error }}
    </StateMessage>

    <StateMessage v-if="loading && items.length === 0" kind="loading" class="mt-6">
      Loading…
    </StateMessage>
    <StateMessage
      v-else-if="!loading && !error && items.length === 0"
      kind="empty"
      data-testid="products-empty"
      class="mt-6"
    >
      {{ search.trim() ? 'No products match your search.' : 'There are no products yet.' }}
    </StateMessage>

    <div v-if="items.length > 0" class="mt-6">
      <DataTable caption="Products">
        <template #head>
          <tr>
            <th scope="col">SKU</th>
            <th scope="col">Name</th>
            <th scope="col">Unit</th>
            <th scope="col" class="text-right">Price</th>
            <th scope="col" class="text-right">Cost</th>
            <th scope="col" class="text-right">Reorder at</th>
            <th scope="col">Status</th>
            <th v-if="canManage" scope="col"><span class="sr-only">Actions</span></th>
          </tr>
        </template>
        <tr v-for="product in items" :key="product.id" data-testid="product-row">
          <td class="font-mono">{{ product.sku }}</td>
          <td>{{ product.name }}</td>
          <td>{{ product.unitOfMeasure }}</td>
          <td class="text-right">{{ formatMoney(product.sellingPrice, product.currency) }}</td>
          <td class="text-right">{{ formatMoney(product.costPrice, product.currency) }}</td>
          <td class="text-right">{{ formatNumber(product.reorderPoint) }}</td>
          <td>
            <StatusBadge :tone="product.isActive ? 'ok' : 'neutral'">{{
              product.isActive ? 'Active' : 'Inactive'
            }}</StatusBadge>
          </td>
          <td v-if="canManage" class="text-right whitespace-nowrap">
            <template v-if="confirming === product.id">
              <span class="mr-2">Deactivate {{ product.name }}?</span>
              <BaseButton
                variant="linkDanger"
                class="mr-2"
                :disabled="deactivating === product.id"
                @click="deactivate(product)"
              >
                Yes, deactivate
              </BaseButton>
              <BaseButton variant="link" @click="confirming = null">Cancel</BaseButton>
            </template>
            <template v-else>
              <RouterLink
                v-if="product.isActive"
                :to="`/products/${product.id}/edit`"
                :class="[buttonClass('link'), 'mr-3']"
              >
                Edit
              </RouterLink>
              <BaseButton
                v-if="product.isActive"
                variant="link"
                :aria-label="`Deactivate ${product.name}`"
                @click="confirming = product.id"
              >
                Deactivate
              </BaseButton>
            </template>
          </td>
        </tr>
      </DataTable>

      <ListPager :page="page" :page-count="pageCount" @go="goTo" />
    </div>
  </section>
</template>
