import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { resolve } from 'path'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react()
  ],
  server: {
    port: 5273,
    proxy: {
      '/api': {
        target: 'http://192.168.31.200:9100',
        changeOrigin: true,
      },
      '/signin-oidc': {
        target: 'http://192.168.31.200:9100',
        changeOrigin: true,
      },
    },
  },
  css: {
    preprocessorOptions: {
      less: {
        javascriptEnabled: true
      }
    },
  },
  resolve: {
    alias: {
      '@': resolve(__dirname, 'src'),
      '~': resolve(__dirname, './'),
    }
  },
  build: {
    manifest: true,
    rollupOptions: {
      output: {
        sourcemap: false
      }
    }
  }
})
