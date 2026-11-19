import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'
import { loadEnv } from 'vite'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_")
  const apiTarget = env.VITE_API_PROXY_TARGET || "http://localhost:8080"

  return {
    plugins: [react()],
    test: {
      include: ["src/**/*.{test,spec}.{ts,tsx}"],
      environment: 'jsdom',
      setupFiles: './src/test-setup.ts',
      restoreMocks: true,
    },
    server: {
      strictPort: true,
      proxy: {
        "/payments": { target: apiTarget, changeOrigin: true },
        "/balances": { target: apiTarget, changeOrigin: true },
      },
    },
  }
})
