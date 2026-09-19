import { defineConfig } from 'vite'

export default defineConfig({
  // Fable emits JS next to nothing we hand-write; `build/client` is the compiler output root.
  root: '.',
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
