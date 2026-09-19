import { defineConfig } from 'vite'

// Single-chunk build for single-file hosting. Pixi's lazy WebGL/WebGPU backends
// are dynamic imports; inlining them is what collapses the output to one script.
export default defineConfig({
  publicDir: 'public',
  build: {
    outDir: 'dist',
    target: 'es2020',
    sourcemap: false,
    emptyOutDir: true,
    rollupOptions: {
      output: { inlineDynamicImports: true }
    }
  }
})
