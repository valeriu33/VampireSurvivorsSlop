import { defineConfig } from 'vite'

export default defineConfig({
  // Fable emits JS next to nothing we hand-write; `build/client` is the compiler output root.
  root: '.',
  // GitHub project Pages serve from /<repo>/, so asset URLs need that prefix.
  // Set by the Pages workflow; defaults to root for local dev and other hosts.
  base: process.env.PUBLIC_BASE || '/',
  publicDir: 'public',
  server: {
    host: true,        // expose on LAN so a real phone can hit the dev server
    port: 5173
  },
  build: {
    outDir: 'dist',
    target: 'es2020',
    sourcemap: true,
    chunkSizeWarningLimit: 1500
  }
})
