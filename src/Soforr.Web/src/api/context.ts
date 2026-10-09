import { inject, type InjectionKey } from 'vue'
import type { ApiClient } from './client'

/** The API client the application provides to its screens; tests provide a fake one. */
export const apiKey: InjectionKey<ApiClient> = Symbol('api')

export function useApi(): ApiClient {
  const client = inject(apiKey)
  if (!client) {
    throw new Error('No API client was provided to the application.')
  }
  return client
}
