import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  outputDir: 'artifacts/playwright',
  use: {
    baseURL: 'http://127.0.0.1:5173',
    viewport: { width: 1440, height: 960 },
    channel: process.env.PLAYWRIGHT_CHROME_CHANNEL || undefined,
    screenshot: 'only-on-failure',
    trace: 'retain-on-failure',
  },
  webServer: {
    command: 'npm run dev:web',
    url: 'http://127.0.0.1:5173',
    reuseExistingServer: !process.env.CI,
  },
})
