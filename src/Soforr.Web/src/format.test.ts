import { describe, expect, it } from 'vitest'
import { formatMoney, formatNumber } from './format'

describe('formatMoney', () => {
  it('shows an amount with its currency', () => {
    expect(formatMoney(1250, 'USD')).toMatch(/1,250\.00/)
    expect(formatMoney('99.5', 'USD')).toMatch(/99\.50/)
  })

  it('shows a dash for an absent amount, and the raw value for one that is not a number', () => {
    expect(formatMoney(null, 'USD')).toBe('—')
    expect(formatMoney(undefined, 'USD')).toBe('—')
    expect(formatMoney('', 'USD')).toBe('—')
    expect(formatMoney('abc', 'USD')).toBe('abc')
  })

  it('does not fail on a currency code it does not know', () => {
    expect(formatMoney(5, 'ZZZZ')).toContain('5')
  })
})

describe('formatNumber', () => {
  it('formats quantities and shows a dash for none', () => {
    expect(formatNumber(1234.5)).toBe('1,234.5')
    expect(formatNumber(null)).toBe('—')
  })
})
