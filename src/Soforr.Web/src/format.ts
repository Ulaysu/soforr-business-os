/**
 * Shows an amount the way a person reads it. The server sends amounts as numbers (or numeric text) in the organization's currency;
 * this only formats them for display and never calculates with them: totals, prices and stock are the server's business.
 */
export function formatMoney(value: number | string | null | undefined, currency: string): string {
  if (value === null || value === undefined || value === '') {
    return '—'
  }
  const amount = Number(value)
  if (!Number.isFinite(amount)) {
    return String(value)
  }
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(amount)
  } catch {
    // An unknown currency code: show the number and the code.
    return `${amount} ${currency}`
  }
}

/** A plain number (a quantity, a reorder point) for display; an absent value is a dash. */
export function formatNumber(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === '') {
    return '—'
  }
  const number = Number(value)
  return Number.isFinite(number)
    ? new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 }).format(number)
    : String(value)
}
