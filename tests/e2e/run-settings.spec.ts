import { expect, test } from '@playwright/test'
import type { DesktopBridge, RunEvent, StartRunRequest } from '../../shared/types'

test('each pane retains its own model, effort and limits after reload', async ({ page }) => {
  await page.goto('/')
  await page.getByLabel('프로젝트 설계 모델', { exact: true }).selectOption('opus')
  await page.getByLabel('프로젝트 설계 사고 강도', { exact: true }).selectOption('high')
  await page.getByRole('button', { name: '프로젝트 설계 실행 설정', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('작업 권한', { exact: true }).selectOption('plan')
  await dialog.getByLabel('최대 턴 수', { exact: true }).fill('0')
  await dialog.getByRole('button', { name: '설정 저장', exact: true }).click()
  await expect(dialog).toBeVisible()
  await dialog.getByLabel('최대 턴 수', { exact: true }).fill('12')
  await dialog.getByLabel('비용 한도 (USD)', { exact: true }).fill('2.5')
  await dialog.getByRole('button', { name: '설정 저장', exact: true }).click()
  await expect(page.getByLabel('코드 작성 모델', { exact: true })).toHaveValue('default')
  await expect(page.getByLabel('코드 작성 사고 강도', { exact: true })).toHaveValue('default')
  await page.reload()
  await expect(page.getByLabel('프로젝트 설계 모델', { exact: true })).toHaveValue('opus')
  await expect(page.getByLabel('프로젝트 설계 사고 강도', { exact: true })).toHaveValue('high')
  await page.getByRole('button', { name: '프로젝트 설계 실행 설정', exact: true }).click()
  await expect(dialog.getByLabel('작업 권한', { exact: true })).toHaveValue('plan')
  await expect(dialog.getByLabel('최대 턴 수', { exact: true })).toHaveValue('12')
  await expect(dialog.getByLabel('비용 한도 (USD)', { exact: true })).toHaveValue('2.5')
  await page.keyboard.press('Escape')
  await page.getByLabel('프로젝트 설계 모델', { exact: true }).selectOption('haiku')
  await expect(page.getByLabel('프로젝트 설계 사고 강도', { exact: true })).toHaveValue('default')
  await expect(page.getByLabel('프로젝트 설계 사고 강도', { exact: true })).toBeDisabled()
  await page.setViewportSize({ width: 1024, height: 720 })
  expect(await page.evaluate(() => [...document.querySelectorAll<HTMLElement>('.composer-toolbar')].every((node) => node.scrollWidth <= node.clientWidth + 1))).toBe(true)
})

test('CLI model capabilities drive choices and all selected settings reach the run request', async ({ page }) => {
  await page.addInitScript(() => {
    const createdAt = '2026-09-16T00:00:00.000Z'
    const listeners = new Set<(event: RunEvent) => void>()
    const requests: StartRunRequest[] = []
    const testWindow = window as Window & { mightyClaude?: DesktopBridge; __mightyRequests?: StartRunRequest[] }
    testWindow.__mightyRequests = requests
    testWindow.mightyClaude = {
      isNative: true,
      async loadState() {
        return { version: 1, workspaces: [{ id: 'workspace-1', name: 'Test', path: '/tmp/test', createdAt }], sessions: [{ id: 'session-1', workspaceId: 'workspace-1', title: 'Claude 1', kind: 'claude', status: 'idle', model: 'default', logs: [], createdAt }], activeWorkspaceId: 'workspace-1', activeSessionId: 'session-1', layout: 'grid', theme: 'dark', sidebarWidth: 252 }
      },
      async saveState() {},
      async pickWorkspace() { return null },
      async getRuntimeInfo() {
        return { platform: 'darwin', appVersion: 'test', claudeAvailable: true, modelCatalog: { source: 'cli', detail: 'CLI 모델 목록', models: [
          { value: 'default', displayName: 'Claude 설정 따름', description: '설정 유지' },
          { value: 'company-sonnet', displayName: 'Company Sonnet (approved deployment)', description: 'Organization model', supportsEffort: true, supportedEffortLevels: ['low', 'high'] },
        ] } }
      },
      async startRun(request) {
        requests.push(request)
        for (const listener of listeners) listener({ type: 'status', sessionId: request.sessionId, status: 'running' })
      },
      async stopRun() {},
      onRunEvent(listener) { listeners.add(listener); return () => { listeners.delete(listener) } },
      windowAction() {},
    }
  })
  await page.goto('/')
  await page.getByLabel('Claude 1 모델', { exact: true }).selectOption('company-sonnet')
  await expect(page.getByLabel('Claude 1 모델', { exact: true }).locator('option:checked')).toHaveText('Company Sonnet (approved deployment)')
  const effort = page.getByLabel('Claude 1 사고 강도', { exact: true })
  expect(await effort.locator('option').evaluateAll((options) => options.map((option) => (option as HTMLOptionElement).value))).toEqual(['default', 'low', 'high'])
  await effort.selectOption('high')
  await page.getByRole('button', { name: 'Claude 1 실행 설정', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('작업 권한', { exact: true }).selectOption('acceptEdits')
  await dialog.getByLabel('최대 턴 수', { exact: true }).fill('9')
  await dialog.getByLabel('비용 한도 (USD)', { exact: true }).fill('1.25')
  await dialog.getByRole('button', { name: '설정 저장', exact: true }).click()
  await page.getByRole('textbox', { name: 'Claude 1 메시지 입력' }).fill('A test request to the bridge fixture only')
  await page.getByRole('button', { name: 'Claude 1 메시지 보내기' }).click()
  const requests = await page.evaluate(() => (window as Window & { __mightyRequests?: StartRunRequest[] }).__mightyRequests)
  expect(requests).toHaveLength(1)
  expect(requests?.[0]).toMatchObject({ model: 'company-sonnet', settings: { effort: 'high', permissionMode: 'acceptEdits', maxTurns: 9, maxBudgetUsd: 1.25 } })
  await expect(effort).toBeDisabled()
  await expect(page.getByLabel('Claude 1 모델', { exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Claude 1 실행 설정', exact: true })).toBeDisabled()
})
