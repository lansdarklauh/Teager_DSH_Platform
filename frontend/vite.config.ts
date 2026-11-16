import { fileURLToPath, URL } from 'node:url'
import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'

// 开发环境：/api 代理到本地后端（含 SSE 与 WebSocket 终端），生产环境由 Nginx 反代
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const backend = env.DEV_BACKEND_URL || 'http://localhost:5600'
  return {
    plugins: [vue()],
    resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
    server: {
      host: '0.0.0.0',
      port: Number(env.DEV_FRONTEND_PORT || 3680),
      strictPort: true,
      proxy: {
        '/api': { target: backend, changeOrigin: false, ws: true },
        '/health': backend
      }
    },
    build: {
      outDir: 'dist',
      chunkSizeWarningLimit: 1500,
      rollupOptions: {
        output: {
          manualChunks(id) { if (id.includes('node_modules')) return 'vendor' }
        }
      }
    }
  }
})
