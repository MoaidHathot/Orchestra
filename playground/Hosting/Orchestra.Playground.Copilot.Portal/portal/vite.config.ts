/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    // Mermaid emits large lazy-loaded diagram chunks (notably ELK). Keep the
    // build warning threshold aligned with those known vendor chunks.
    chunkSizeWarningLimit: 1600,
    rollupOptions: {
      output: {
        entryFileNames: 'assets/[name].js',
        chunkFileNames: (chunkInfo) =>
          chunkInfo.name.startsWith('vendor-') ? 'assets/[name].js' : 'assets/[name]-[hash].js',
        assetFileNames: 'assets/[name][extname]',
        manualChunks: {
          // Split large vendor libraries into separate cached chunks so that
          // app code changes don't force Vite to re-process these heavy deps.
          'vendor-mermaid': ['mermaid'],
          'vendor-react': ['react', 'react-dom'],
        },
      },
    },
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5100',
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    setupFiles: ['./src/test-setup.ts'],
  },
})
