<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { useApi } from '../api/context'
import { networkProblem, toProblem } from '../api/problem'
import BaseButton from '../components/BaseButton.vue'
import FormField from '../components/FormField.vue'
import PageHeader from '../components/PageHeader.vue'
import StateMessage from '../components/StateMessage.vue'
import { buttonClass, inputClass } from '../components/styles'
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
</script>

<template>
  <section class="max-w-xl">
    <PageHeader>{{ isEdit ? 'Edit product' : 'New product' }}</PageHeader>

    <p v-if="!canManage" data-testid="not-allowed" class="mt-4 text-sm">
      You can look at products but not change them.
      <RouterLink :class="buttonClass('link')" to="/products">Back to the products</RouterLink>
    </p>

    <StateMessage v-else-if="loading" kind="loading" class="mt-4">Loading…</StateMessage>

    <form v-else class="mt-6 space-y-4" novalidate @submit.prevent="submit">
      <FormField id="sku" label="SKU" :error="fieldError('sku')" error-test-id="error-sku">
        <template #default="{ aria }">
          <input id="sku" v-model="form.sku" :class="inputClass" autocomplete="off" v-bind="aria" />
        </template>
      </FormField>
      <FormField id="name" label="Name" :error="fieldError('name')" error-test-id="error-name">
        <template #default="{ aria }">
          <input
            id="name"
            v-model="form.name"
            :class="inputClass"
            autocomplete="off"
            v-bind="aria"
          />
        </template>
      </FormField>
      <FormField
        id="unit"
        label="Unit of measure"
        :error="fieldError('unitOfMeasure')"
        error-test-id="error-unitOfMeasure"
      >
        <template #default="{ aria }">
          <input
            id="unit"
            v-model="form.unitOfMeasure"
            placeholder="for example bag, kg, box"
            :class="inputClass"
            autocomplete="off"
            v-bind="aria"
          />
        </template>
      </FormField>
      <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <FormField
          id="price"
          :label="`Selling price${currency ? ` (${currency})` : ''}`"
          :error="fieldError('sellingPrice')"
          error-test-id="error-sellingPrice"
        >
          <template #default="{ aria }">
            <input
              id="price"
              v-model="form.sellingPrice"
              inputmode="decimal"
              :class="inputClass"
              autocomplete="off"
              v-bind="aria"
            />
          </template>
        </FormField>
        <FormField
          id="cost"
          :label="`Cost price${currency ? ` (${currency})` : ''}`"
          :error="fieldError('costPrice')"
          error-test-id="error-costPrice"
        >
          <template #default="{ aria }">
            <input
              id="cost"
              v-model="form.costPrice"
              inputmode="decimal"
              :class="inputClass"
              autocomplete="off"
              v-bind="aria"
            />
          </template>
        </FormField>
        <FormField
          id="reorder"
          label="Reorder at"
          :error="fieldError('reorderPoint')"
          error-test-id="error-reorderPoint"
        >
          <template #default="{ aria }">
            <input
              id="reorder"
              v-model="form.reorderPoint"
              inputmode="decimal"
              :class="inputClass"
              autocomplete="off"
              v-bind="aria"
            />
          </template>
        </FormField>
        <FormField
          id="lead"
          label="Supplier lead time (days)"
          :error="fieldError('reorderLeadTimeDays')"
          error-test-id="error-reorderLeadTimeDays"
        >
          <template #default="{ aria }">
            <input
              id="lead"
              v-model="form.reorderLeadTimeDays"
              inputmode="numeric"
              :class="inputClass"
              autocomplete="off"
              v-bind="aria"
            />
          </template>
        </FormField>
      </div>

      <StateMessage v-if="message" kind="error" data-testid="form-error">{{
        message
      }}</StateMessage>

      <div class="flex items-center gap-4">
        <BaseButton type="submit" :disabled="saving">{{ saving ? 'Saving…' : 'Save' }}</BaseButton>
        <RouterLink :class="buttonClass('link')" to="/products">Cancel</RouterLink>
      </div>
    </form>
  </section>
</template>
