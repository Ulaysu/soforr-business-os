<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import type { components } from '../api/schema'
import BaseButton from '../components/BaseButton.vue'
import ConfirmDialog from '../components/ConfirmDialog.vue'
import DataTable from '../components/DataTable.vue'
import PageHeader from '../components/PageHeader.vue'
import ListPager from '../components/ListPager.vue'
import StateMessage from '../components/StateMessage.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { buttonClass, inputClass, panelClass } from '../components/styles'
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
const root = ref<HTMLElement | null>(null)
const confirming = ref<string | null>(null)
const deactivating = ref<string | null>(null)
let loadGeneration = 0

const pageCount = computed(() => Math.max(1, Math.ceil(totalCount.value / pageSize)))
const confirmingProduct = computed(() =>
  items.value.find((product) => product.id === confirming.value),
)
const summary = computed(() => {
  if (totalCount.value === 0) return ''
  const from = (page.value - 1) * pageSize + 1
  const to = Math.min(totalCount.value, page.value * pageSize)
  return `Showing ${from}–${to} of ${totalCount.value}`
})

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

/**
 * After a deactivation the row the person acted on is gone or has no actions, so focus would fall to the page. Move it to the next row's
 * action (or the one before, at the end of the list), and to the heading when no row has an action.
 */
async function restoreFocusAfterDeactivation(index: number) {
  await nextTick()
  const actions = Array.from(
    root.value?.querySelectorAll<HTMLElement>('[data-testid="product-row"]') ?? [],
  ).map((row) => row.querySelector<HTMLElement>('td:last-child a, td:last-child button'))
  const target =
    actions.slice(index).find((control) => control) ?? actions.reverse().find((control) => control)
  ;(target ?? root.value?.querySelector<HTMLElement>('h1'))?.focus()
}

async function deactivate(product: Product) {
  const index = items.value.findIndex((item) => item.id === product.id)
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
  if (succeeded) {
    await load()
    await restoreFocusAfterDeactivation(index)
  }
}

// Below the xl breakpoint each row becomes a stacked block (name first, then labelled facts) instead of a squeezed table row; the
// roles on the cells keep the table semantics when the display type changes.
const rowClass =
  'max-xl:grid max-xl:grid-cols-2 sm:max-xl:grid-cols-4 max-xl:gap-x-4 max-xl:gap-y-3 max-xl:p-4'
const cellClass =
  'max-xl:block max-xl:p-0! max-xl:before:mb-0.5 max-xl:before:block max-xl:before:text-xs max-xl:before:font-normal max-xl:before:text-muted max-xl:before:content-[attr(data-label)]'
</script>

<template>
  <section ref="root">
    <PageHeader>
      Products
      <template #description>Everything you sell, with its price, cost and reorder point.</template>
      <template #actions>
        <RouterLink v-if="canManage" to="/products/new" :class="buttonClass('primary')">
          <svg
            aria-hidden="true"
            viewBox="0 0 24 24"
            class="size-4"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
          >
            <path d="M12 5v14M5 12h14" />
          </svg>
          New product
        </RouterLink>
      </template>
    </PageHeader>

    <div class="mt-6 flex flex-wrap items-center gap-x-6 gap-y-3">
      <div class="relative w-full sm:max-w-sm">
        <label class="sr-only" for="search">Search products</label>
        <svg
          aria-hidden="true"
          viewBox="0 0 24 24"
          class="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted"
          fill="none"
          stroke="currentColor"
          stroke-width="1.8"
          stroke-linecap="round"
        >
          <circle cx="11" cy="11" r="6.5" />
          <path d="M16 16l4 4" />
        </svg>
        <input
          id="search"
          v-model="search"
          type="search"
          placeholder="Search by SKU or name"
          :class="[inputClass, 'mt-0 pl-9']"
        />
      </div>
      <label class="flex cursor-pointer items-center gap-3 text-sm">
        <input
          id="includeInactive"
          v-model="includeInactive"
          type="checkbox"
          class="peer sr-only"
        />
        <span
          aria-hidden="true"
          class="relative h-5 w-9 shrink-0 rounded-full bg-control motion-safe:transition-colors peer-checked:bg-accent peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent after:absolute after:top-0.5 after:left-0.5 after:size-4 after:rounded-full after:bg-raised after:shadow-panel after:content-[''] motion-safe:after:transition-transform peer-checked:after:translate-x-4"
        ></span>
        Show inactive
      </label>
      <p v-if="totalCount > 0" class="text-sm text-muted sm:ml-auto" data-testid="products-count">
        {{ totalCount === 1 ? '1 product' : `${totalCount} products` }}
      </p>
    </div>

    <div v-if="error" class="mt-4 flex flex-wrap items-center gap-3">
      <StateMessage kind="error" data-testid="products-error" class="flex-1">
        {{ error }}
      </StateMessage>
      <BaseButton variant="secondary" :disabled="loading" @click="load">Try again</BaseButton>
    </div>

    <div v-if="loading && items.length === 0" class="mt-6" :class="panelClass">
      <StateMessage kind="loading" class="sr-only">Loading…</StateMessage>
      <div aria-hidden="true" class="divide-y divide-line">
        <div v-for="n in 5" :key="n" class="flex items-center gap-4 px-4 py-4">
          <span class="h-4 w-20 rounded bg-line motion-safe:animate-pulse"></span>
          <span class="h-4 flex-1 rounded bg-line motion-safe:animate-pulse"></span>
          <span class="hidden h-4 w-24 rounded bg-line motion-safe:animate-pulse sm:block"></span>
        </div>
      </div>
    </div>
    <div
      v-else-if="!loading && !error && items.length === 0"
      data-testid="products-empty"
      class="mt-6 rounded-lg border border-dashed border-control bg-raised px-6 py-14 text-center"
    >
      <p class="text-base font-medium">
        {{ search.trim() ? 'No products match your search.' : 'There are no products yet.' }}
      </p>
      <p class="mt-1 text-sm text-muted">
        {{
          search.trim()
            ? 'Check the SKU or name, or turn on Show inactive.'
            : canManage
              ? 'Add your first product to start tracking its price and stock.'
              : 'Products will appear here once someone adds them.'
        }}
      </p>
      <p v-if="!search.trim() && canManage" class="mt-5">
        <RouterLink to="/products/new" :class="buttonClass('primary')">New product</RouterLink>
      </p>
    </div>

    <div v-if="items.length > 0" class="mt-6" :aria-busy="loading">
      <DataTable caption="Products" :class="loading && 'opacity-60'">
        <template #head>
          <tr role="row">
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
        <tr
          v-for="product in items"
          :key="product.id"
          role="row"
          data-testid="product-row"
          :class="rowClass"
        >
          <td
            role="cell"
            data-label="SKU"
            :class="[cellClass, 'order-2 font-mono text-xs text-muted']"
          >
            {{ product.sku }}
          </td>
          <td
            role="cell"
            :class="[
              cellClass,
              'order-1 col-span-full min-w-40 text-base font-semibold xl:text-sm',
            ]"
          >
            {{ product.name }}
          </td>
          <td role="cell" data-label="Unit" :class="[cellClass, 'order-3 text-muted']">
            {{ product.unitOfMeasure }}
          </td>
          <td
            role="cell"
            data-label="Price"
            :class="[cellClass, 'order-4 font-semibold whitespace-nowrap xl:text-right']"
          >
            {{ formatMoney(product.sellingPrice, product.currency) }}
          </td>
          <td
            role="cell"
            data-label="Cost"
            :class="[cellClass, 'order-5 whitespace-nowrap text-muted xl:text-right']"
          >
            {{ formatMoney(product.costPrice, product.currency) }}
          </td>
          <td
            role="cell"
            data-label="Reorder at"
            :class="[cellClass, 'order-6 whitespace-nowrap text-muted xl:text-right']"
          >
            {{ formatNumber(product.reorderPoint) }}
          </td>
          <td role="cell" data-label="Status" :class="[cellClass, 'order-7']">
            <StatusBadge :tone="product.isActive ? 'ok' : 'neutral'">{{
              product.isActive ? 'Active' : 'Inactive'
            }}</StatusBadge>
          </td>
          <td
            v-if="canManage"
            role="cell"
            :class="[
              cellClass,
              'order-8 col-span-full max-xl:flex max-xl:gap-1 xl:text-right xl:whitespace-nowrap',
            ]"
          >
            <template v-if="product.isActive">
              <RouterLink
                :to="`/products/${product.id}/edit`"
                :class="[buttonClass('link'), 'max-xl:min-h-10 max-xl:px-3']"
                :aria-label="`Edit ${product.name}`"
              >
                Edit
              </RouterLink>
              <BaseButton
                variant="linkDanger"
                class="max-xl:min-h-10 max-xl:px-3"
                :aria-label="`Deactivate ${product.name}`"
                @click="confirming = product.id"
              >
                Deactivate
              </BaseButton>
            </template>
          </td>
        </tr>
        <template #footer>
          <ListPager
            v-if="pageCount > 1"
            :page="page"
            :page-count="pageCount"
            :summary="summary"
            @go="goTo"
          />
          <span v-else class="text-sm text-muted" data-testid="page-summary">{{ summary }}</span>
        </template>
      </DataTable>
    </div>

    <ConfirmDialog
      v-if="confirmingProduct"
      :title="`Deactivate ${confirmingProduct.name}?`"
      confirm-label="Yes, deactivate"
      tone="danger"
      :busy="deactivating === confirmingProduct.id"
      @confirm="deactivate(confirmingProduct)"
      @cancel="confirming = null"
    >
      <p>
        <span class="font-mono text-xs">{{ confirmingProduct.sku }}</span> will be marked inactive.
        It is hidden from this list unless “Show inactive” is on, and it can no longer be edited
        here.
      </p>
    </ConfirmDialog>
  </section>
</template>
