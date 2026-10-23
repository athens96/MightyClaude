/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url';
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

const repo = (path: string) => fileURLToPath(new URL('../../../' + path, import.meta.url));

// The built page loads only its own files; the dev server needs React's inline refresh preamble, so the policy is a build-only tag.
const contentSecurityPolicy: Plugin = {
  name: 'mighty-csp',
  apply: 'build',
  transformIndexHtml: () => [{
    tag: 'meta',
    attrs: { 'http-equiv': 'Content-Security-Policy', content: "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'none'" },
    injectTo: 'head-prepend',
  }],
};

// The repo's locale files and design tokens are the single source: imported where they live, never copied.
// `base: './'` keeps every asset path relative, so the host can serve dist/ from any folder (WebShell.cs).
export default defineConfig({
  base: './',
  // One tsconfig for every file, also the phone app's modules imported from mobile/src (whose own extends Expo's).
  tsconfig: './tsconfig.json',
  plugins: [react(), contentSecurityPolicy],
  resolve: {
    alias: {
      '@locales': repo('locales'),
      '@tokens': repo('native/contracts/fixtures'),
      // Pure modules of the phone app (mobile/src/lib), which import each other as `@/…`.
      '@mobile': repo('mobile/src'),
      '@/': repo('mobile/src') + '/',
    },
  },
  server: { fs: { allow: [repo('')] } },
  build: { outDir: 'dist', emptyOutDir: true, target: 'es2022', sourcemap: false },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
});
