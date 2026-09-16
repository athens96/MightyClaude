import { resolve } from 'node:path'
import { defineConfig } from 'electron-vite'
import { rendererConfig } from './vite.shared'

export default defineConfig({
  main: {
    build: {
      rollupOptions: {
        input: resolve('electron/main/index.ts'),
        output: { format: 'es', entryFileNames: 'index.js' },
      },
    },
  },
  preload: {
    build: {
      rollupOptions: {
        input: resolve('electron/preload/index.ts'),
        output: { format: 'cjs', entryFileNames: 'index.cjs' },
      },
    },
  },
  renderer: rendererConfig(),
})
