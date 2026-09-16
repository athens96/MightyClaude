import type { AppSnapshot, DesktopBridge, RunEvent } from '../../shared/types'
import { DEFAULT_RUN_SETTINGS, fallbackModelCatalog } from '../../shared/claude-options'
import { fallbackProviderRuntime, PROVIDER_IDS } from '../../shared/provider-options'
import { makeId } from './state'

const STORAGE_KEY = 'mightyclaude.preview.v1'

function initialPreview(): AppSnapshot {
  const createdAt = new Date().toISOString()
  const workspaceId = 'preview-workspace'
  return {
    version: 1,
    workspaces: [{ id: workspaceId, name: 'MightyClaude', path: '~/Projects/MightyClaude', createdAt }],
    sessions: [
      { id: 'preview-plan', workspaceId, title: '프로젝트 설계', kind: 'claude', status: 'idle', model: 'default', settings: { ...DEFAULT_RUN_SETTINGS }, logs: [], createdAt },
      { id: 'preview-build', workspaceId, title: '코드 작성', kind: 'claude', status: 'idle', model: 'default', settings: { ...DEFAULT_RUN_SETTINGS }, logs: [], createdAt },
      { id: 'preview-shell', workspaceId, title: '터미널', kind: 'shell', status: 'idle', model: 'default', settings: { ...DEFAULT_RUN_SETTINGS }, logs: [], createdAt },
    ],
    activeWorkspaceId: workspaceId,
    activeSessionId: 'preview-plan',
    layout: 'grid',
    theme: 'dark',
    sidebarWidth: 252,
  }
}

export function createBrowserBridge(): DesktopBridge {
  const listeners = new Set<(event: RunEvent) => void>()
  const emit = (event: RunEvent) => listeners.forEach((listener) => listener(event))
  return {
    isNative: false,
    async loadState() {
      const saved = localStorage.getItem(STORAGE_KEY)
      if (saved) {
        try { return JSON.parse(saved) as AppSnapshot } catch { /* Recover a malformed preview snapshot. */ }
      }
      return initialPreview()
    },
    async saveState(snapshot) { localStorage.setItem(STORAGE_KEY, JSON.stringify(snapshot)) },
    async pickWorkspace() { return null },
    async getRuntimeInfo() {
      return { platform: 'browser', appVersion: '0.1.0', claudeAvailable: false, modelCatalog: fallbackModelCatalog(true), providers: PROVIDER_IDS.map((id) => fallbackProviderRuntime(id, true)), mods: { status: 'preview', minimumVersion: '2.1.271', detail: '데스크톱 앱에서 Claude Mods를 연결할 수 있습니다.' } }
    },
    async startRun(request) {
      emit({
        sessionId: request.sessionId,
        type: 'log',
        entry: {
          id: makeId('log'),
          kind: 'system',
          text: '브라우저 미리보기에서는 명령을 실행하지 않습니다. 데스크톱 앱에서 선택한 CLI 실행기를 연결하세요.',
          timestamp: new Date().toISOString(),
        },
      })
      emit({ sessionId: request.sessionId, type: 'status', status: 'idle' })
    },
    async stopRun(sessionId) { emit({ sessionId, type: 'status', status: 'stopped' }) },
    onRunEvent(listener) { listeners.add(listener); return () => { listeners.delete(listener) } },
    windowAction() { /* Native window actions are only exposed in the desktop shell. */ },
  }
}

export const bridge: DesktopBridge = (window as Window & { mightyClaude?: DesktopBridge }).mightyClaude ?? createBrowserBridge()
