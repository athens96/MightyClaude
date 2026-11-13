/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url';
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

const repo = (path: string) => fileURLToPath(new URL('../../../' + path, import.meta.url));

// The built page loads only its own files; the dev server needs React's inline refresh preamble, so the policy is a build-only tag.
// The Rust + Tauri app's build (`vite build --mode tauri`, native/tauri) also lets the page reach Tauri's IPC, which it fetches
// from http://ipc.localhost on Windows (ipc: elsewhere); the C# app's build keeps the policy as it was.
function contentSecurityPolicy(tauri: boolean): Plugin {
  const ipc = tauri ? "; connect-src 'self' ipc: http://ipc.localhost" : '';
  return {
    name: 'mighty-csp',
    apply: 'build',
    transformIndexHtml: () => [{
      tag: 'meta',
      attrs: { 'http-equiv': 'Content-Security-Policy', content: "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-src https://*.html.mighty.invalid; object-src 'none'; base-uri 'none'; form-action 'none'" + ipc },
      injectTo: 'head-prepend',
    }],
  };
}

// The repo's locale files and design tokens are the single source: imported where they live, never copied.
// `base: './'` keeps every asset path relative, so the host can serve dist/ from any folder (WebShell.cs). The Tauri build goes
// to dist-tauri/, so it never replaces the dist/ the C# build ships as Assets\Web.
export default defineConfig(({ mode }) => ({
  base: './',
  // One tsconfig for every file, also the phone app's modules imported from mobile/src (whose own extends Expo's).
  tsconfig: './tsconfig.json',
  plugins: [react(), contentSecurityPolicy(mode === 'tauri')],
  resolve: {
    alias: {
      '@locales': repo('locales'),
      '@tokens': repo('native/contracts/fixtures'),
      // The shared parity vectors (graph-vectors.json), read by the Mighty diagram's tests.
      '@contracts': repo('native/contracts'),
      // Pure modules of the phone app (mobile/src/lib), which import each other as `@/…`.
      '@mobile': repo('mobile/src'),
      '@/': repo('mobile/src') + '/',
    },
  },
  server: { fs: { allow: [repo('')] } },
  build: { outDir: mode === 'tauri' ? 'dist-tauri' : 'dist', emptyOutDir: true, target: 'es2022', sourcemap: false },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
}));
