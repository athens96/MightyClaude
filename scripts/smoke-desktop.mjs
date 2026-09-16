import assert from 'node:assert/strict'
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { resolve, join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { _electron as electron, expect } from '@playwright/test'

// A fresh app profile avoids reading or changing a developer's saved workspaces.
// This test never submits a model prompt, installs a plugin, or starts sharing.
const temporary = await mkdtemp(join(tmpdir(), 'mighty-desktop-smoke-'))
const profile = join(temporary, 'profile')
const workspace = join(temporary, 'smoke-workspace')
await mkdir(profile)
await mkdir(workspace)
await mkdir('artifacts', { recursive: true })
await writeFile(join(temporary, 'package.json'), JSON.stringify({ name: 'mighty-claude-smoke', version: '0.1.0', type: 'module', main: 'bootstrap.mjs' }))
await writeFile(join(temporary, 'bootstrap.mjs'), [
  "import { app } from 'electron';",
  `app.setPath('userData', ${JSON.stringify(profile)});`,
  `await import(${JSON.stringify(pathToFileURL(resolve('out/main/index.js')).href)});`,
].join('\n'))

let application
const errors = []
async function launch() {
  application = await electron.launch({ args: [temporary], cwd: process.cwd() })
  const page = await application.firstWindow()
  page.on('pageerror', (error) => errors.push(error.message))
  await page.getByRole('button', { name: '설정', exact: true }).waitFor()
  assert.equal(await application.evaluate(({ app }) => app.getPath('userData')), profile)
  assert.equal(await page.evaluate(() => typeof window.require), 'undefined')
  return page
}

try {
  let page = await launch()
  const runtime = await page.evaluate(() => window.mightyClaude.getRuntimeInfo())
  assert.deepEqual(runtime.providers.map((provider) => provider.id), ['claude', 'codex', 'gemini'])
  const codexModel = runtime.providers.find((provider) => provider.id === 'codex').modelCatalog.models.find((model) => model.supportedEffortLevels?.includes('high'))
  if (process.env.MIGHTY_REQUIRE_PROVIDERS === '1') {
    assert.equal(runtime.providers.find((provider) => provider.id === 'codex').available, true)
    assert.equal(runtime.providers.find((provider) => provider.id === 'codex').modelCatalog.source, 'cli')
    assert.equal(runtime.providers.find((provider) => provider.id === 'gemini').available, true)
    assert.ok(codexModel, 'The installed Codex model catalog must expose a model supporting High')
    console.log('PASS: installed Codex and Gemini detected; Codex model/list reached native IPC without a user turn')
  }
  await application.evaluate(({ dialog }, selectedPath) => {
    dialog.showOpenDialog = async () => ({ canceled: false, filePaths: [selectedPath] })
  }, workspace)
  await page.getByRole('button', { name: '프로젝트 폴더 열기', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Claude 1 실행 창' })).toBeVisible()
  await page.getByLabel('Claude 1 모델', { exact: true }).selectOption('sonnet')
  await page.getByLabel('Claude 1 사고 강도', { exact: true }).selectOption('high')
  await page.getByRole('button', { name: 'Claude 1 실행 설정', exact: true }).click()
  let settings = page.getByRole('dialog')
  if (process.env.MIGHTY_REQUIRE_MODEL_CATALOG === '1') {
    await expect(settings.getByText('CLI에서 확인', { exact: true })).toBeVisible({ timeout: 15_000 })
    console.log('PASS: actual installed Claude CLI model catalog reached the desktop renderer')
  }
  await settings.getByLabel('작업 권한', { exact: true }).selectOption('plan')
  await settings.getByLabel('최대 턴 수', { exact: true }).fill('10')
  await settings.getByLabel('비용 한도 (USD)', { exact: true }).fill('2.5')
  await settings.getByRole('button', { name: '설정 저장', exact: true }).click()
  await page.getByRole('button', { name: '터미널', exact: true }).click()
  const command = page.getByRole('textbox', { name: '터미널 1 명령 입력' })
  await command.fill('echo MIGHTY_NATIVE_OK')
  await page.getByRole('button', { name: '터미널 1 명령 실행' }).click()
  await expect(page.getByRole('log', { name: '터미널 1 출력' }).locator('.log-output')).toContainText('MIGHTY_NATIVE_OK')
  await expect(page.getByRole('region', { name: '터미널 1 실행 창' }).getByText('완료', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'artifacts/mightyclaude-desktop.png' })
  await page.getByRole('button', { name: '새 Claude', exact: true }).click()
  await page.getByLabel('Claude 2 실행기', { exact: true }).selectOption('codex')
  if (codexModel) {
    await page.getByLabel('Claude 2 모델', { exact: true }).selectOption(codexModel.value)
    await page.getByLabel('Claude 2 사고 강도', { exact: true }).selectOption('high')
  }
  // Quit before the renderer's save debounce can settle.
  await page.getByRole('button', { name: '밝은 테마로 전환' }).click()
  await application.close()
  application = undefined
  page = await launch()
  await expect(page.getByRole('button', { name: 'smoke-workspace', exact: true })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Claude 2 실행 창' })).toBeVisible()
  await expect(page.getByLabel('Claude 1 모델', { exact: true })).toHaveValue('sonnet')
  await expect(page.getByLabel('Claude 1 사고 강도', { exact: true })).toHaveValue('high')
  await expect(page.getByLabel('Claude 2 실행기', { exact: true })).toHaveValue('codex')
  await expect(page.getByLabel('Claude 2 모델', { exact: true })).toHaveValue(codexModel?.value ?? 'default')
  await expect(page.getByLabel('Claude 2 사고 강도', { exact: true })).toHaveValue(codexModel ? 'high' : 'default')
  await page.getByRole('button', { name: 'Claude 1 실행 설정', exact: true }).click()
  settings = page.getByRole('dialog')
  await expect(settings.getByLabel('작업 권한', { exact: true })).toHaveValue('plan')
  await expect(settings.getByLabel('최대 턴 수', { exact: true })).toHaveValue('10')
  await expect(settings.getByLabel('비용 한도 (USD)', { exact: true })).toHaveValue('2.5')
  await page.screenshot({ path: 'artifacts/mightyclaude-run-settings.png' })
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: '어두운 테마로 전환' })).toBeVisible()
  await expect(page.getByRole('log', { name: '터미널 1 출력' })).toContainText('MIGHTY_NATIVE_OK')
  await page.getByRole('button', { name: '원격 연결', exact: true }).click()
  const remoteDialog = page.getByRole('dialog')
  await expect(remoteDialog.getByRole('heading', { name: '이 컴퓨터 공유' })).toBeVisible()
  await expect(remoteDialog.getByRole('heading', { name: '다른 컴퓨터 연결' })).toBeVisible()
  await expect(remoteDialog.getByRole('button', { name: '공유 시작', exact: true })).toBeDisabled()
  const remoteState = await page.evaluate(() => window.mightyClaude.getRemoteState())
  assert.equal(remoteState.host.enabled, false)
  assert.equal(remoteState.host.token, undefined)
  await page.screenshot({ path: 'artifacts/mightyclaude-remote.png' })
  await page.keyboard.press('Escape')
  await page.getByRole('button', { name: '새 Claude', exact: true }).click()
  await page.getByLabel('Claude 3 실행기', { exact: true }).selectOption('gemini')
  await expect(page.getByLabel('Claude 3 사고 강도', { exact: true })).toBeDisabled()
  await page.screenshot({ path: 'artifacts/mightyclaude-providers.png' })
  await commandFor(page).fill('node -e "setInterval(() => {}, 1000)"')
  await page.getByRole('button', { name: '터미널 1 명령 실행' }).click()
  await page.getByRole('button', { name: '터미널 1 실행 중지' }).click()
  await expect(page.getByRole('region', { name: '터미널 1 실행 창' }).getByText('중지됨', { exact: true })).toBeVisible()
  assert.deepEqual(errors, [])
  console.log('PASS: native preload isolation, three providers, per-pane settings persistence, remote IPC/dialog with sharing off, shell execution, stop, and restart persistence')
} finally {
  if (application) await application.close()
  await rm(temporary, { recursive: true, force: true })
}

function commandFor(page) {
  return page.getByRole('textbox', { name: '터미널 1 명령 입력' })
}
