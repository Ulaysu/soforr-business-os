<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import { canManageProducts, useSession } from '../stores/session'

const props = defineProps<{ id?: string }>()

const api = useApi()
const session = useSession()
const router = useRouter()
const canManage = computed(() => canManageProducts(session.state.account?.role))
const isEdit = computed(() => props.id !== undefined)

const form = reactive({
  sku: '',
  name: '',
  unitOfMeasure: '',
  sellingPrice: '',
  costPrice: '',
  reorderPoint: '',
  reorderLeadTimeDays: '',
})
const currency = ref('')
const loading = ref(false)
const saving = ref(false)
const message = ref<string | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})

function text(value: number | string | null | undefined): string {
  return value === null || value === undefined ? '' : String(value)
}

// A route parameter can change while Vue Router reuses this view. Ignore any response from an older product.
let generation = 0

function resetForm() {
  saving.value = false
  form.sku = ''
  form.name = ''
  form.unitOfMeasure = ''
  form.sellingPrice = ''
  form.costPrice = ''
  form.reorderPoint = ''
  form.reorderLeadTimeDays = ''
  currency.value = ''
  message.value = null
  fieldErrors.value = {}
}

watch(
  () => props.id,
  async (productId) => {
    const current = ++generation
    resetForm()
    if (productId === undefined) {
      loading.value = false
      return
    }

    loading.value = true
    try {
      const { data, error, response } = await api.GET('/api/v1/products/{id}', {
        params: { path: { id: productId } },
      })
      if (current !== generation) return
      if (response.ok && data) {
        currency.value = data.currency
        form.sku = data.sku
        form.name = data.name
        form.unitOfMeasure = data.unitOfMeasure
        form.sellingPrice = text(data.sellingPrice)
        form.costPrice = text(data.costPrice)
        form.reorderPoint = text(data.reorderPoint)
        form.reorderLeadTimeDays = text(data.reorderLeadTimeDays)
      } else {
        message.value = toProblem(error, response.status).message
      }
    } catch {
      if (current === generation) {
        message.value = networkProblem().message
      }
    } finally {
      if (current === generation) loading.value = false
    }
  },
  { immediate: true },
)

function fieldError(name: string): string | null {
  return fieldErrors.value[name]?.[0] ?? null
}

/** A blank box is "not set"; anything else is sent as the number typed, and the server decides whether it is valid. */
function optionalNumber(value: string): number | null {
  return value.trim() === '' ? null : Number(value)
}

/** Catches the obvious mistakes before sending. This is for the person's convenience only: the server validates everything again. */
function checkLocally(): Record<string, string[]> {
  const problems: Record<string, string[]> = {}
  for (const [name, label] of [
    ['sku', 'SKU'],
    ['name', 'Name'],
    ['unitOfMeasure', 'Unit'],
  ] as const) {
    if (form[name].trim() === '') {
      problems[name] = [`${label} is required.`]
    }
  }
  if (form.sellingPrice.trim() === '' || Number.isNaN(Number(form.sellingPrice))) {
    problems.sellingPrice = ['Enter the selling price as a number.']
  }
  if (form.costPrice.trim() === '' || Number.isNaN(Number(form.costPrice))) {
    problems.costPrice = ['The cost price is required. Enter 0 if there is none.']
  }
  for (const name of ['reorderPoint', 'reorderLeadTimeDays'] as const) {
    if (form[name].trim() !== '' && Number.isNaN(Number(form[name]))) {
      problems[name] = ['Enter a number or leave it empty.']
    }
  }
  if ((form.reorderPoint.trim() === '') !== (form.reorderLeadTimeDays.trim() === '')) {
    problems.reorderLeadTimeDays = ['Enter both the reorder point and the lead time, or neither.']
  }
  return problems
}

async function submit() {
  message.value = null
  fieldErrors.value = checkLocally()
  if (Object.keys(fieldErrors.value).length > 0) {
    return
  }

  const body = {
    sku: form.sku.trim(),
    name: form.name.trim(),
    unitOfMeasure: form.unitOfMeasure.trim(),
    sellingPrice: Number(form.sellingPrice),
    costPrice: optionalNumber(form.costPrice),
    reorderPoint: optionalNumber(form.reorderPoint),
    reorderLeadTimeDays: optionalNumber(form.reorderLeadTimeDays),
  }

  saving.value = true
  const current = generation
  const productId = props.id
  try {
    const result =
      productId === undefined
        ? await api.POST('/api/v1/products', { body })
        : await api.PUT('/api/v1/products/{id}', { params: { path: { id: productId } }, body })

    if (current !== generation) return
    if (result.response.ok) {
      await router.push({ name: 'products' })
      return
    }
    const problem = toProblem(result.error, result.response.status)
    fieldErrors.value = problem.fieldErrors
    // The server answers a taken SKU with a message but no field; the box it is about is the SKU.
    if (problem.code === 'product.sku_taken') {
      fieldErrors.value = { sku: [problem.message] }
    }
    message.value =
      Object.keys(fieldErrors.value).length > 0
        ? 'Some of the details need attention.'
        : problem.message
  } catch {
    if (current === generation) message.value = networkProblem().message
  } finally {
    if (current === generation) saving.value = false
  }
}

const inputClass =
  'mt-1 w-full rounded border border-slate-300 bg-transparent px-3 py-2 dark:border-slate-700'
</script>

<template>
  <section class="max-w-xl">
    <h1 class="text-2xl font-semibold">{{ isEdit ? 'Edit product' : 'New product' }}</h1>

    <p v-if="!canManage" data-testid="not-allowed" class="mt-4 text-sm">
      You can look at products but not change them.
      <RouterLink class="underline" to="/products">Back to the products</RouterLink>
    </p>

    <p v-else-if="loading" class="mt-4 text-sm text-slate-500">Loading…</p>

    <form v-else class="mt-6 space-y-4" novalidate @submit.prevent="submit">
      <div>
        <label class="block text-sm font-medium" for="sku">SKU</label>
        <input id="sku" v-model="form.sku" :class="inputClass" autocomplete="off" />
        <p
          v-if="fieldError('sku')"
          data-testid="error-sku"
          class="mt-1 text-sm text-red-600 dark:text-red-400"
        >
          {{ fieldError('sku') }}
        </p>
      </div>
      <div>
        <label class="block text-sm font-medium" for="name">Name</label>
        <input id="name" v-model="form.name" :class="inputClass" autocomplete="off" />
        <p
          v-if="fieldError('name')"
          data-testid="error-name"
          class="mt-1 text-sm text-red-600 dark:text-red-400"
        >
          {{ fieldError('name') }}
        </p>
      </div>
      <div>
        <label class="block text-sm font-medium" for="unit">Unit of measure</label>
        <input
          id="unit"
          v-model="form.unitOfMeasure"
          placeholder="for example bag, kg, box"
          :class="inputClass"
          autocomplete="off"
        />
        <p
          v-if="fieldError('unitOfMeasure')"
          data-testid="error-unitOfMeasure"
          class="mt-1 text-sm text-red-600 dark:text-red-400"
        >
          {{ fieldError('unitOfMeasure') }}
        </p>
      </div>
      <div class="grid grid-cols-2 gap-4">
        <div>
          <label class="block text-sm font-medium" for="price"
            >Selling price{{ currency ? ` (${currency})` : '' }}</label
          >
          <input
            id="price"
            v-model="form.sellingPrice"
            inputmode="decimal"
            :class="inputClass"
            autocomplete="off"
          />
          <p
            v-if="fieldError('sellingPrice')"
            data-testid="error-sellingPrice"
            class="mt-1 text-sm text-red-600 dark:text-red-400"
          >
            {{ fieldError('sellingPrice') }}
          </p>
        </div>
        <div>
          <label class="block text-sm font-medium" for="cost"
            >Cost price{{ currency ? ` (${currency})` : '' }}</label
          >
          <input
            id="cost"
            v-model="form.costPrice"
            inputmode="decimal"
            :class="inputClass"
            autocomplete="off"
          />
          <p
            v-if="fieldError('costPrice')"
            data-testid="error-costPrice"
            class="mt-1 text-sm text-red-600 dark:text-red-400"
          >
            {{ fieldError('costPrice') }}
          </p>
        </div>
        <div>
          <label class="block text-sm font-medium" for="reorder">Reorder at</label>
          <input
            id="reorder"
            v-model="form.reorderPoint"
            inputmode="decimal"
            :class="inputClass"
            autocomplete="off"
          />
          <p
            v-if="fieldError('reorderPoint')"
            data-testid="error-reorderPoint"
            class="mt-1 text-sm text-red-600 dark:text-red-400"
          >
            {{ fieldError('reorderPoint') }}
          </p>
        </div>
        <div>
          <label class="block text-sm font-medium" for="lead">Supplier lead time (days)</label>
          <input
            id="lead"
            v-model="form.reorderLeadTimeDays"
            inputmode="numeric"
            :class="inputClass"
            autocomplete="off"
          />
          <p
            v-if="fieldError('reorderLeadTimeDays')"
            data-testid="error-reorderLeadTimeDays"
            class="mt-1 text-sm text-red-600 dark:text-red-400"
          >
            {{ fieldError('reorderLeadTimeDays') }}
          </p>
        </div>
      </div>

      <p
        v-if="message"
        role="alert"
        data-testid="form-error"
        class="text-sm text-red-600 dark:text-red-400"
      >
        {{ message }}
      </p>

      <div class="flex items-center gap-4">
        <button
          type="submit"
          :disabled="saving"
          class="rounded bg-blue-600 px-4 py-2 font-medium text-white hover:bg-blue-700 disabled:opacity-60"
        >
          {{ saving ? 'Saving…' : 'Save' }}
        </button>
        <RouterLink class="underline" to="/products">Cancel</RouterLink>
      </div>
    </form>
  </section>
</template>
