export type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'link' | 'linkDanger'

const buttonBase =
  'inline-flex items-center justify-center gap-2 text-sm font-medium whitespace-nowrap motion-safe:transition-colors disabled:cursor-not-allowed disabled:opacity-60'

const buttonVariants: Record<ButtonVariant, string> = {
  primary:
    'rounded-md bg-accent px-4 py-2 text-on-accent shadow-panel hover:bg-accent-strong hover:text-on-accent',
  secondary:
    'rounded-md border border-control bg-raised px-4 py-2 text-ink shadow-panel hover:bg-hover',
  danger: 'rounded-md bg-danger px-4 py-2 text-surface shadow-panel hover:opacity-90',
  link: 'rounded-md px-2 py-1 text-accent-strong hover:bg-accent-soft disabled:hover:bg-transparent',
  linkDanger: 'rounded-md px-2 py-1 text-danger hover:bg-danger-soft disabled:hover:bg-transparent',
}

/** The classes of a button, for a `<button>` (BaseButton) or for a RouterLink that looks like one. */
export function buttonClass(variant: ButtonVariant = 'primary'): string {
  return `${buttonBase} ${buttonVariants[variant]}`
}

/** A text box, select or text area. `aria-invalid` (set by FormField) turns the border red. */
export const inputClass =
  'mt-1.5 block w-full rounded-md border border-control bg-raised px-3 py-2 text-sm text-ink placeholder:text-muted motion-safe:transition-colors hover:border-muted disabled:opacity-60 aria-invalid:border-danger'

/** A bordered raised panel: the container for a group of form fields or a block of facts. */
export const panelClass = 'rounded-lg border border-line bg-raised shadow-panel'
