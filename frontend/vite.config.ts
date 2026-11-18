import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "VITE_")
  const apiTarget = env.VITE_API_PROXY_TARGET || "http://localhost:8080"

  return {
    plugins: [react()],
    server: {
      strictPort: true,
      proxy: {
        "/payments": { target: apiTarget, changeOrigin: true },
        "/balances": { target: apiTarget, changeOrigin: true },
      },
    },
  }
})
