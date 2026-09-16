import { expect, test, type Page } from '@playwright/test'
import type { AppSnapshot, DesktopBridge, ProviderRuntime, RemoteState, RunEvent, StartRunRequest } from '../../shared/types'

async function installBridge(page: Page) {
  await page.addInitScript(() => {
    type FixtureWindow = Window & { mightyClaude?: DesktopBridge; __requests?: StartRunRequest[]; __saved?: AppSnapshot; __connections?: unknown[]; __shares?: unknown[]; __refreshes?: string[] }
    const target = window as FixtureWindow
    const createdAt = '2026-09-16T00:00:00.000Z'
    const listeners = new Set<(event: RunEvent) => void>()
    const providers: ProviderRuntime[] = [
      { id: 'claude', name: 'Claude Code', available: true, detail: 'Fixture', modelCatalog: { source: 'cli', detail: 'Fixture', models: [{ value: 'default', displayName: 'Claude default', description: 'Fixture' }] }, capabilities: { effort: true, permissionModes: ['manual', 'plan', 'acceptEdits'], maxTurns: true, maxBudgetUsd: true, resume: true } },
      { id: 'codex', name: 'Codex CLI', available: true, detail: 'Fixture', modelCatalog: { source: 'cli', detail: 'Fixture', models: [{ value: 'default', displayName: 'Codex default', description: 'Fixture' }, { value: 'local-codex', displayName: 'Local Codex', description: 'Local only', supportsEffort: true, supportedEffortLevels: ['low', 'high'] }] }, capabilities: { effort: true, permissionModes: ['manual', 'acceptEdits'], maxTurns: false, maxBudgetUsd: false, resume: true } },
      { id: 'gemini', name: 'Gemini CLI', available: true, detail: 'Fixture', modelCatalog: { source: 'fallback', detail: 'Fixture', models: [{ value: 'default', displayName: 'Gemini default', description: 'Fixture' }] }, capabilities: { effort: false, permissionModes: ['manual', 'plan', 'acceptEdits'], maxTurns: false, maxBudgetUsd: false, resume: true } },
    ]
    const local = { id: 'workspace-local', name: 'Local project', path: '/tmp/local-project', createdAt }
    const runtime = { platform: 'darwin' as const, appVersion: 'fixture', claudeAvailable: true, providers }
    const remoteRuntime = { ...runtime, platform: 'win32' as const, providers: providers.map((provider) => provider.id === 'codex' ? { ...provider, modelCatalog: { source: 'cli' as const, detail: 'Remote catalog', models: [{ value: 'default', displayName: 'Remote default', description: 'Fixture' }, { value: 'remote-codex', displayName: 'Remote Codex model', description: 'Remote machine only', supportsEffort: true, supportedEffortLevels: ['high' as const] }] } } : provider) }
    const peer = { id: 'peer-workspace', name: 'Remote project', path: 'C:\\Projects\\remote-project', createdAt }
    const remote: RemoteState = { tailscale: { available: true, addresses: ['100.80.0.1'], detail: 'Tailscale connected' }, host: { enabled: false, workspaceIds: [], activeRuns: 0 }, connections: [] }
    let saved: AppSnapshot = { version: 1, workspaces: [local], sessions: [{ id: 'session-local', workspaceId: local.id, title: 'Claude 1', kind: 'claude', provider: 'claude', model: 'default', resumeId: 'claude-prior-session', status: 'idle', settings: { effort: 'high', permissionMode: 'plan', maxTurns: 10, maxBudgetUsd: 2 }, logs: [], createdAt }], activeWorkspaceId: local.id, activeSessionId: 'session-local', layout: 'grid', theme: 'dark', sidebarWidth: 252 }
    target.__requests = []
    target.__connections = []
    target.__shares = []
    target.__refreshes = []
    target.mightyClaude = {
      isNative: true,
      async loadState() { return saved },
      async saveState(snapshot) { saved = structuredClone(snapshot); target.__saved = saved },
      async pickWorkspace() { return null },
      async getRuntimeInfo() { return runtime },
      async startRun(request) { target.__requests!.push(request); for (const listener of listeners) listener({ type: 'status', sessionId: request.sessionId, status: 'running' }) },
      async stopRun(sessionId) { for (const listener of listeners) listener({ type: 'status', sessionId, status: 'stopped' }) },
      onRunEvent(listener) { listeners.add(listener); return () => { listeners.delete(listener) } },
      windowAction() {},
      async getRemoteState() { return structuredClone(remote) },
      async startSharing(request) { target.__shares!.push(request); remote.host = { enabled: true, workspaceIds: request.workspaceIds, activeRuns: 0, address: 'http://100.80.0.1:43137', token: 'host-secret-must-not-be-in-snapshot', port: 43137 }; return structuredClone(remote) },
      async stopSharing() { remote.host = { enabled: false, workspaceIds: [], activeRuns: 0 }; return structuredClone(remote) },
      async connectRemote(request) { target.__connections!.push(request); remote.connections = [{ id: 'connection-1', name: request.name, address: request.address, hostName: 'Build computer', hostId: 'host-fixture', status: 'connected', workspaces: [peer], runtime: remoteRuntime }]; return structuredClone(remote) },
      async refreshRemote(id) { target.__refreshes!.push(id); remote.connections = remote.connections.map((connection) => connection.id === id ? { ...connection, status: 'connected' } : connection); return structuredClone(remote) },
      async disconnectRemote() { remote.connections = remote.connections.map((connection) => ({ ...connection, status: 'disconnected' })); return structuredClone(remote) },
      async importRemoteWorkspace() { return { ...peer, id: 'imported-workspace', remote: { connectionId: 'connection-1', workspaceId: peer.id, hostName: 'Build computer' } } },
    }
  })
}

test('switching provider clears previous model, limits and resume identity, and forwards the selected provider', async ({ page }) => {
  await installBridge(page)
  await page.goto('/')
  await page.getByLabel('Claude 1 실행기', { exact: true }).selectOption('codex')
  await expect(page.getByLabel('Claude 1 모델', { exact: true })).toHaveValue('default')
  await page.getByRole('button', { name: 'Claude 1 실행 설정', exact: true }).click()
  const settings = page.getByRole('dialog')
  await expect(settings.getByLabel('최대 턴 수', { exact: true })).toBeDisabled()
  await expect(settings.getByLabel('비용 한도 (USD)', { exact: true })).toBeDisabled()
  await expect(settings.getByLabel('최대 턴 수', { exact: true })).toHaveValue('')
  await expect(settings.getByLabel('작업 권한', { exact: true })).toHaveValue('manual')
  await page.keyboard.press('Escape')
  await page.getByLabel('Claude 1 모델', { exact: true }).selectOption('local-codex')
  await page.getByLabel('Claude 1 사고 강도', { exact: true }).selectOption('high')
  await page.getByRole('textbox', { name: 'Claude 1 메시지 입력' }).fill('Fixture only; no provider is called')
  await page.getByRole('button', { name: 'Claude 1 메시지 보내기' }).click()
  const requests = await page.evaluate(() => (window as Window & { __requests?: StartRunRequest[] }).__requests)
  expect(requests?.[0]).toMatchObject({ provider: 'codex', model: 'local-codex', settings: { effort: 'high', permissionMode: 'manual', maxTurns: null, maxBudgetUsd: null } })
  expect(requests?.[0]?.resumeId).toBeUndefined()
  await expect(page.getByLabel('Claude 1 실행기', { exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Claude 1 실행 중지' }).click()
  await page.getByLabel('Claude 1 실행기', { exact: true }).selectOption('gemini')
  await expect(page.getByLabel('Claude 1 사고 강도', { exact: true })).toBeDisabled()
})

test('sharing requires a selected workspace and remote import uses the remote provider catalog without saving secrets', async ({ page }) => {
  await page.clock.install()
  await installBridge(page)
  await page.goto('/')
  await page.getByRole('button', { name: '원격 연결', exact: true }).click()
  let dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('button', { name: '공유 시작', exact: true })).toBeDisabled()
  await dialog.getByRole('checkbox').first().check()
  await dialog.getByRole('button', { name: '공유 시작', exact: true }).click()
  await expect(dialog.getByRole('button', { name: '공유 중지', exact: true })).toBeVisible()
  await dialog.getByLabel('연결 이름', { exact: true }).fill('Build computer')
  await dialog.getByLabel('원격 주소', { exact: true }).fill('http://100.80.0.2:43137')
  await dialog.getByLabel('연결 키', { exact: true }).fill('remote-secret-must-not-be-in-snapshot')
  await dialog.getByRole('button', { name: '컴퓨터 연결', exact: true }).click()
  await dialog.getByRole('button', { name: 'Remote project 원격 워크스페이스 열기', exact: true }).click()
  await page.keyboard.press('Escape')
  await page.getByLabel('Claude 1 실행기', { exact: true }).selectOption('codex')
  await page.getByLabel('Claude 1 모델', { exact: true }).selectOption('remote-codex')
  await expect(page.getByLabel('Claude 1 모델', { exact: true }).locator('option:checked')).toHaveText('Remote Codex model')
  await expect(page.getByRole('region', { name: 'Claude 1 실행 창' })).toContainText('Build computer')
  await page.getByRole('textbox', { name: 'Claude 1 메시지 입력' }).fill('Run on the peer fixture')
  await page.getByRole('button', { name: 'Claude 1 메시지 보내기' }).click()
  const requests = await page.evaluate(() => (window as Window & { __requests?: StartRunRequest[] }).__requests)
  expect(requests?.[0]).toMatchObject({ workspaceId: 'imported-workspace', provider: 'codex', model: 'remote-codex' })
  await expect.poll(async () => page.evaluate(() => (window as Window & { __saved?: AppSnapshot }).__saved?.workspaces.length)).toBe(2)
  const snapshot = await page.evaluate(() => JSON.stringify((window as Window & { __saved?: AppSnapshot }).__saved))
  expect(snapshot).not.toContain('secret-must-not-be-in-snapshot')
  expect(snapshot).toContain('connection-1')
  await page.getByRole('button', { name: 'Claude 1 실행 중지' }).click()
  await page.getByRole('button', { name: '원격 연결', exact: true }).click()
  dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Build computer 연결 해제', exact: true }).click()
  await page.keyboard.press('Escape')
  await page.getByRole('textbox', { name: 'Claude 1 메시지 입력' }).fill('Must not fall back to local execution')
  await expect(page.getByRole('button', { name: 'Claude 1 메시지 보내기' })).toBeDisabled()
  const refreshCount = await page.evaluate(() => (window as Window & { __refreshes?: string[] }).__refreshes?.length)
  await page.clock.fastForward(11_000)
  await expect(page.getByRole('button', { name: 'Claude 1 메시지 보내기' })).toBeDisabled()
  expect(await page.evaluate(() => (window as Window & { __refreshes?: string[] }).__refreshes?.length)).toBe(refreshCount)
  await page.getByRole('button', { name: '원격 연결', exact: true }).click()
  await page.getByRole('button', { name: 'Build computer 새로고침', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Claude 1 메시지 보내기' })).toBeEnabled()
})
