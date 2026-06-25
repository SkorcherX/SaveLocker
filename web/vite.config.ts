import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    host: '0.0.0.0',
    port: process.env.PORT ? parseInt(process.env.PORT) : 5173,
    proxy: {
      '/api': 'http://localhost:5179',
      '/art': 'http://localhost:5179',
    },
  },
  build: {
    outDir: 'dist',
  },
})
