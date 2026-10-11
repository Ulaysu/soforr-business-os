<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import type { components } from '../api/schema'
import { formatNumber } from '../format'
import { canManageStock, useSession } from '../stores/session'

type StockLevel = components['schemas']['StockLevelDto']
type Movement = components['schemas']['MovementDto']

const props = defineProps<{ id: string }>()

const api = useApi()
const session = useSession()
const canManage = computed(() => canManageStock(session.state.account?.role))

const level = ref<StockLevel | null>(null)
const movements = ref<Movement[]>([])
const movementTotal = ref(0)
const loadError = ref<string | null>(null)

const quantityChange = ref('')
const note = ref('')
const saving = ref(false)
const formError = ref<string | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})
const done = ref<string | null>(null)

// The id of the adjustment being entered. It is sent with the request, so sending it again (a retry after a timeout or a double
// click) cannot adjust the stock twice; a new one is chosen only after an adjustment was recorded.
let adjustmentId = crypto.randomUUID()

// Bumped every time the route's product changes. A response is applied only if no newer product was opened meanwhile, so a late
// answer for product A can never overwrite product B (the same component is reused when the route parameter changes).
let generation = 0

async function loadLevel(productId: string, current: number) {
  try {
    const { data, error, response } = await api.GET('/api/v1/inventory/stock/{productId}', {
      params: { path: { productId } },
    })
    if (current !== generation) {
      return
    }
    if (response.ok && data) {
      level.value = data
      loadError.value = null
    } else {
      loadError.value = toProblem(error, response.status).message
    }
  } catch {
    if (current === generation) {
      loadError.value = networkProblem().message
    }
  }
}

async function loadMovements(productId: string, current: number) {
  if (!canManage.value) {
    return
  }
  try {
    const { data, response } = await api.GET('/api/v1/inventory/stock/{productId}/movements', {
      params: { path: { productId }, query: { page: 1, pageSize: 25 } },
    })
    if (current === generation && response.ok && data) {
      movements.value = data.items
      movementTotal.value = Number(data.totalCount)
    }
  } catch {
    // The history is secondary; the level and the form still work.
  }
}

watch(
  () => props.id,
  async (productId) => {
    generation += 1
    const current = generation
    // Nothing of the previous product stays visible, and the form starts again with a new adjustment id.
    level.value = null
    movements.value = []
    movementTotal.value = 0
    loadError.value = null
    quantityChange.value = ''
    note.value = ''
    formError.value = null
    fieldErrors.value = {}
    done.value = null
    adjustmentId = crypto.randomUUID()
    await loadLevel(productId, current)
    await loadMovements(productId, current)
  },
  { immediate: true },
)

/** "SaleCompleted" is shown as "Sale completed". */
function reasonText(reason: string): string {
  const words = reason.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

function when(value: string): string {
  return new Date(value).toLocaleString()
}

function fieldError(name: string): string | null {
  return fieldErrors.value[name]?.[0] ?? null
}

async function submit() {
  formError.value = null
  done.value = null
  fieldErrors.value = {}

  // Only avoids sending an unreadable form; the server checks the amount, the note and whether the stock can take it.
  const change = Number(quantityChange.value)
  if (quantityChange.value.trim() === '' || Number.isNaN(change) || change === 0) {
    fieldErrors.value = {
      quantityChange: ['Enter how much to add (a number) or remove (a negative number).'],
    }
    return
  }
  if (note.value.trim() === '') {
    fieldErrors.value = { note: ['A note is required: say why the stock is being changed.'] }
    return
  }

  saving.value = true
  const productId = props.id
  const current = generation
  try {
    const { error, response } = await api.POST('/api/v1/inventory/adjustments', {
      body: { productId, quantityChange: change, note: note.value.trim(), adjustmentId },
    })
    if (current !== generation) {
      return // another product was opened while this was sent; its screen is not this one's
    }
    if (response.ok) {
      done.value = 'The stock was adjusted.'
      quantityChange.value = ''
      note.value = ''
      adjustmentId = crypto.randomUUID()
      await loadLevel(productId, current)
      await loadMovements(productId, current)
      return
    }
    const problem = toProblem(error, response.status)
    fieldErrors.value = problem.fieldErrors
    formError.value = problem.message
  } catch {
    if (current !== generation) {
      return
    }
    // Nothing is known about whether it was recorded: the same adjustment id is kept, so trying again is safe.
    formError.value = `${networkProblem().message} It is safe to try again: the same adjustment will not be applied twice.`
  } finally {
    saving.value = false
  }
}

const inputClass =
  'mt-1 w-full rounded border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700'
</script>

<template>
  <section>
    <RouterLink class="text-sm underline" to="/stock">Back to the stock</RouterLink>

    <p
      v-if="loadError"
      role="alert"
      data-testid="stock-error"
      class="mt-4 text-sm text-red-600 dark:text-red-400"
    >
      {{ loadError }}
    </p>

    <template v-if="level">
      <h1 class="mt-3 text-2xl font-semibold">{{ level.name }}</h1>
      <p class="text-sm text-slate-500">
        <span class="font-mono">{{ level.sku }}</span> ·
        {{ level.unitOfMeasure }}
      </p>

      <dl class="mt-6 grid max-w-md grid-cols-2 gap-y-2 text-sm">
        <dt class="text-slate-500">On hand</dt>
        <dd data-testid="on-hand" class="text-right text-lg font-semibold">
          {{ formatNumber(level.quantity) }}
        </dd>
        <dt class="text-slate-500">Reorder at</dt>
        <dd class="text-right">{{ formatNumber(level.reorderPoint) }}</dd>
        <dt class="text-slate-500">Supplier lead time</dt>
        <dd class="text-right">
          {{ level.reorderLeadTimeDays === null ? '—' : `${level.reorderLeadTimeDays} days` }}
        </dd>
        <dt class="text-slate-500">Status</dt>
        <dd class="text-right" data-testid="low">{{ level.isLow ? 'Low' : 'OK' }}</dd>
      </dl>

      <form v-if="canManage" class="mt-8 max-w-md space-y-4" novalidate @submit.prevent="submit">
        <h2 class="text-lg font-semibold">Adjust the stock</h2>
        <div>
          <label class="block text-sm font-medium" for="quantityChange"
            >Change (use a minus sign to remove)</label
          >
          <input
            id="quantityChange"
            v-model="quantityChange"
            inputmode="decimal"
            :class="inputClass"
            autocomplete="off"
          />
          <p
            v-if="fieldError('quantityChange')"
            data-testid="error-quantityChange"
            class="mt-1 text-sm text-red-600 dark:text-red-400"
          >
            {{ fieldError('quantityChange') }}
          </p>
        </div>
        <div>
          <label class="block text-sm font-medium" for="note">Why (required)</label>
          <input id="note" v-model="note" :class="inputClass" autocomplete="off" />
          <p
            v-if="fieldError('note')"
            data-testid="error-note"
            class="mt-1 text-sm text-red-600 dark:text-red-400"
          >
            {{ fieldError('note') }}
          </p>
        </div>
        <p
          v-if="formError"
          role="alert"
          data-testid="form-error"
          class="text-sm text-red-600 dark:text-red-400"
        >
          {{ formError }}
        </p>
        <p v-if="done" data-testid="done" class="text-sm text-green-700 dark:text-green-400">
          {{ done }}
        </p>
        <button
          type="submit"
          :disabled="saving"
          class="rounded bg-blue-600 px-4 py-2 font-medium text-white hover:bg-blue-700 disabled:opacity-60"
        >
          {{ saving ? 'Saving…' : 'Adjust stock' }}
        </button>
      </form>
      <p v-else class="mt-8 text-sm text-slate-500">You can look at the stock but not change it.</p>

      <section v-if="canManage" class="mt-10">
        <h2 class="text-lg font-semibold">History</h2>
        <p v-if="movements.length === 0" class="mt-2 text-sm text-slate-500">
          No stock movements yet.
        </p>
        <table v-else class="mt-2 w-full text-left text-sm">
          <thead class="border-b border-slate-200 text-slate-500 dark:border-slate-800">
            <tr>
              <th class="py-2 pr-4 font-medium">When</th>
              <th class="py-2 pr-4 text-right font-medium">Change</th>
              <th class="py-2 pr-4 font-medium">Reason</th>
              <th class="py-2 font-medium">Note</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="movement in movements"
              :key="movement.id"
              data-testid="movement-row"
              class="border-b border-slate-100 dark:border-slate-900"
            >
              <td class="py-2 pr-4 whitespace-nowrap">{{ when(movement.occurredAtUtc) }}</td>
              <td class="py-2 pr-4 text-right">
                {{ Number(movement.quantityChange) > 0 ? '+' : ''
                }}{{ formatNumber(movement.quantityChange) }}
              </td>
              <td class="py-2 pr-4">{{ reasonText(movement.reason) }}</td>
              <td class="py-2">{{ movement.note ?? '' }}</td>
            </tr>
          </tbody>
        </table>
        <p v-if="movementTotal > movements.length" class="mt-2 text-sm text-slate-500">
          Showing the latest {{ movements.length }} of {{ movementTotal }}.
        </p>
      </section>
    </template>
  </section>
</template>
