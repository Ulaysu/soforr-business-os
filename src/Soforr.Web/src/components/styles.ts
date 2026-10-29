export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'link' | 'linkDanger'

const buttonBase =
  'inline-flex items-center justify-center text-sm font-medium disabled:cursor-not-allowed disabled:opacity-60'

const buttonVariants: Record<ButtonVariant, string> = {
  primary: 'rounded-md bg-accent px-4 py-2 text-on-accent hover:bg-accent-strong',
  secondary: 'rounded-md border border-control bg-raised px-4 py-2 text-ink hover:bg-accent-soft',
  danger: 'rounded-md bg-danger px-4 py-2 text-surface hover:opacity-90',
  link: 'text-accent underline underline-offset-2 hover:text-accent-strong disabled:no-underline',
  linkDanger: 'text-danger underline underline-offset-2 hover:opacity-80 disabled:no-underline',
}

/** The classes of a button, for a `<button>` (BaseButton) or for a RouterLink that looks like one. */
export function buttonClass(variant: ButtonVariant = 'primary'): string {
  return `${buttonBase} ${buttonVariants[variant]}`
}

/** A text box, select or text area. `aria-invalid` (set by FormField) turns the border red. */
export const inputClass =
  'mt-1 block w-full rounded-md border border-control bg-raised px-3 py-2 text-sm text-ink placeholder:text-muted disabled:opacity-60 aria-invalid:border-danger'
