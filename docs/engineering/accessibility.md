# Accessibility

The baseline for `src/Soforr.Web` is **WCAG 2.1 level AA**. It is part of the Definition of Done for every screen, not a later polish pass. The frontend skills reference a generic checklist that is not in this repository; this document replaces it for Soforr.

## Rules

- **Keyboard.** Everything that can be clicked can be reached and used with the keyboard, in a sensible order. Use real `<button>`, `<a>`, `<input>` and `<select>` elements; avoid `div` with a click handler. Fast entry screens (sales, purchasing) must be usable without a mouse.
- **Focus.** The focus ring is global (`:focus-visible` in `src/styles/main.css`) and must never be removed. After a dialog opens, focus moves into it; when it closes, focus returns to the control that opened it. Use native `<dialog>` with `showModal()`.
- **Labels.** Every input has a visible `<label for>`, or an `sr-only` label for a search box. Help and error text is tied to its input with `aria-describedby`; an invalid input has `aria-invalid="true"`.
- **Errors and status.** A failed load or save is shown in an element with `role="alert"` and says what happened and what to do. Inline field errors sit next to their field. A completed action is announced with `role="status"`.
- **Color is never the only signal.** Severity, stock status, margin and balances always carry text ("Low", "High", "Overdue"). This also follows the Overview specification.
- **Contrast.** Text 4.5:1, large text and control borders 3:1, in light and dark. Use the tokens in `main.css`, whose ratios were checked; check any new pair before adding it.
- **Tables.** Use `<table>` with `<th scope="col">` (and `scope="row"` for row headers), a visible or `sr-only` caption when the heading is not adjacent, right-aligned numerals, and one clearly named link or button per row action (include the item's name in its accessible name).
- **Headings.** One `h1` per screen, no skipped levels.
- **Responsive.** No horizontal page scroll at 320px; wide tables scroll inside their own container.
- **Motion.** Motion only answers an action, and respects `prefers-reduced-motion`.

## Checking a screen

Automated tests assert roles, labels and `role="alert"` messages through the real router (see [testing.md](testing.md)). Before a screen is called done also check by hand, in light and dark:

1. Tab through the whole screen: order is logical, focus is always visible, nothing is a trap.
2. Use the main task with the keyboard only.
3. Zoom to 200% and view at 320px width.
4. Trigger the loading, empty, error and forbidden states.
5. Confirm that no information depends on color alone.

An automated accessibility scanner (axe-core with a Vitest adapter) would be a new dependency and needs owner approval before it is added.
