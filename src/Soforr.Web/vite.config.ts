import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

export default defineConfig(({ mode }) => {
  // The API address for the dev server proxy (SOFORR_API_URL in the environment or in a git-ignored .env.local). The
  // browser only ever talks to the dev server's own origin, so the session cookie and the antiforgery cookie stay
  // same-site, exactly as they will in production (ADR 0002).
  const env = loadEnv(mode, '.', 'SOFORR_')
  const apiTarget = env.SOFORR_API_URL ?? 'https://localhost:7168'

  return {
    plugins: [vue(), tailwindcss()],
    server: {
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true, secure: false },
        '/health': { target: apiTarget, changeOrigin: true, secure: false },
      },
    },
    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.ts'],
    },
  }
})
