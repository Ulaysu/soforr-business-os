import { ref } from 'vue'

export type Theme = 'light' | 'dark'

const storageKey = 'soforr-theme'

function stored(): Theme | null {
  try {
    const value = localStorage.getItem(storageKey)
    return value === 'light' || value === 'dark' ? value : null
  } catch {
    return null
  }
}

function systemTheme(): Theme {
  return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches
    ? 'dark'
    : 'light'
}

const theme = ref<Theme>(stored() ?? systemTheme())

function apply(value: Theme) {
  document.documentElement.dataset.theme = value
}

/** The chosen theme: the saved choice, else the operating system's. Switching is remembered on this device only. */
export function useTheme() {
  apply(theme.value)

  function toggle() {
    theme.value = theme.value === 'dark' ? 'light' : 'dark'
    apply(theme.value)
    try {
      localStorage.setItem(storageKey, theme.value)
    } catch {
      // Storage can be unavailable (private windows); the choice then lasts until the page closes.
    }
  }

  return { theme, toggle }
}
