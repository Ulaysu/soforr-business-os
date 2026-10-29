import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import BaseButton from './BaseButton.vue'
import DataTable from './DataTable.vue'
import FactList from './FactList.vue'
import FormField from './FormField.vue'
import ListPager from './ListPager.vue'
import StateMessage from './StateMessage.vue'
import StatusBadge from './StatusBadge.vue'

describe('FormField', () => {
  function field(error: string | null, hint?: string) {
    return mount(FormField, {
      props: { id: 'sku', label: 'SKU', error, errorTestId: 'error-sku', hint },
      slots: {
        default: (slot: { aria: Record<string, unknown> }) =>
          h('input', { id: 'sku', ...slot.aria }),
      },
    })
  }

  it('ties the label, the hint and the error to the input', () => {
    const wrapper = field('SKU is required.', 'Printed on the label.')
    expect(wrapper.get('label').attributes('for')).toBe('sku')
    const input = wrapper.get('input')
    expect(input.attributes('aria-invalid')).toBe('true')
    expect(input.attributes('aria-describedby')).toBe('sku-hint sku-error')
    expect(wrapper.get('[data-testid="error-sku"]').text()).toBe('SKU is required.')
  })

  it('marks nothing invalid when there is no error', () => {
    const input = field(null).get('input')
    expect(input.attributes('aria-invalid')).toBeUndefined()
    expect(input.attributes('aria-describedby')).toBeUndefined()
  })
})

describe('StateMessage', () => {
  it('announces errors and loading, and leaves empty states quiet', () => {
    expect(mount(StateMessage, { props: { kind: 'error' } }).attributes('role')).toBe('alert')
    expect(mount(StateMessage, { props: { kind: 'loading' } }).attributes('role')).toBe('status')
    expect(mount(StateMessage, { props: { kind: 'empty' } }).attributes('role')).toBeUndefined()
  })
})

describe('ListPager', () => {
  it('is absent for a single page', () => {
    expect(
      mount(ListPager, { props: { page: 1, pageCount: 1 } })
        .find('nav')
        .exists(),
    ).toBe(false)
  })

  it('emits the requested page and disables the ends', async () => {
    const wrapper = mount(ListPager, { props: { page: 1, pageCount: 3 } })
    const [previous, next] = wrapper.findAll('button')
    expect(previous?.attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="page-info"]').text()).toBe('Page 1 of 3')
    await next?.trigger('click')
    expect(wrapper.emitted('go')).toEqual([[2]])
  })
})

describe('BaseButton', () => {
  it('is a plain button unless it is told to submit', () => {
    expect(mount(BaseButton).attributes('type')).toBe('button')
    expect(mount(BaseButton, { props: { type: 'submit' } }).attributes('type')).toBe('submit')
  })

  it('passes disabled and click through to the button', async () => {
    let clicks = 0
    const wrapper = mount(BaseButton, { attrs: { onClick: () => clicks++ } })
    await wrapper.trigger('click')
    expect(clicks).toBe(1)
    expect(mount(BaseButton, { attrs: { disabled: true } }).attributes('disabled')).toBeDefined()
  })
})

describe('StatusBadge, FactList and DataTable', () => {
  it('shows the status as words', () => {
    expect(mount(StatusBadge, { props: { tone: 'warn' }, slots: { default: 'Low' } }).text()).toBe(
      'Low',
    )
  })

  it('lists facts as label and value pairs', () => {
    const wrapper = mount(FactList, {
      props: { facts: [{ label: 'On hand', value: '12', testId: 'on-hand' }] },
    })
    expect(wrapper.get('dt').text()).toBe('On hand')
    expect(wrapper.get('[data-testid="on-hand"]').text()).toBe('12')
  })

  it('gives the table an accessible caption', () => {
    const Host = defineComponent({
      components: { DataTable },
      template:
        '<DataTable caption="Stock levels"><template #head><tr><th scope="col">SKU</th></tr></template><tr><td>A</td></tr></DataTable>',
    })
    const wrapper = mount(Host)
    expect(wrapper.get('caption').text()).toBe('Stock levels')
    expect(wrapper.get('th').attributes('scope')).toBe('col')
  })
})
