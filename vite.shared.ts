import { resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import type { Plugin, UserConfig } from 'vite'

// React refresh needs an inline preamble only while the local dev server runs.
// Packaged builds keep script-src 'self' and have no remote connections.
function developmentCsp(): Plugin {
  return {
    name: 'mighty-development-csp',
    apply: 'serve',
    transformIndexHtml(html) {
      return html
        .replace("script-src 'self'", "script-src 'self' 'unsafe-inline'")
        .replace("connect-src 'self'", "connect-src 'self' ws://127.0.0.1:* ws://localhost:*")
    },
  }
}

export function rendererConfig(): UserConfig {
  return {
    root: resolve('.'),
    base: './',
    plugins: [react(), developmentCsp()],
    resolve: { alias: { '@': resolve('src') } },
    server: { host: '127.0.0.1', port: 5173, strictPort: true },
    build: { minify: 'esbuild', rollupOptions: { input: resolve('index.html') } },
  }
}
