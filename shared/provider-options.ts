import { EFFORT_LEVELS, effortLevelsForModel, fallbackModelCatalog, normalizeRunSettings } from './claude-options'
import type { ClaudeEffort, ClaudeModelCatalog, ClaudeRunSettings, ProviderId, ProviderRuntime } from './types'

export const PROVIDER_IDS: ProviderId[] = ['claude', 'codex', 'gemini']

export function normalizeProvider(value: unknown): ProviderId {
  return value === 'codex' || value === 'gemini' ? value : 'claude'
}

export function providerLabel(id: ProviderId): string {
  return { claude: 'Claude', codex: 'Codex', gemini: 'Gemini' }[id]
}

export function fallbackProviderCatalog(id: ProviderId, preview = false): ClaudeModelCatalog {
  if (id === 'claude') return fallbackModelCatalog(preview)
  return {
    source: preview ? 'preview' : 'fallback',
    detail: `${providerLabel(id)} 공식 모델 이름 예시입니다. 사용 가능 여부는 CLI의 계정·제공자 설정에 따라 달라집니다.`,
    models: id === 'codex' ? [
      { value: 'default', displayName: 'Codex 설정 따름', description: 'Codex 설정 또는 재개한 세션의 모델을 사용합니다.' },
      { value: 'gpt-5.6-sol', displayName: 'GPT-5.6 Sol', description: '공식 Codex 모델 이름입니다. 실제 지원 여부는 모델 목록을 새로고침해 확인하세요.' },
      { value: 'gpt-6-astra', displayName: 'GPT-6 Astra', description: '직접 모델 ID를 지정합니다. 계정의 사용 가능 여부는 CLI에서 확인합니다.' },
    ] : [
      { value: 'default', displayName: 'Gemini 설정 따름', description: 'Gemini CLI 설정을 사용합니다.' },
      { value: 'auto', displayName: 'Auto', description: 'Gemini CLI가 작업에 맞는 모델을 선택합니다.' },
      { value: 'gemini-3-pro-preview', displayName: 'Gemini 3 Pro Preview', description: '공식 모델 ID 예시 · 계정에서 제공하는 모델을 직접 선택할 수 있습니다.' },
      { value: 'gemini-3-flash-preview', displayName: 'Gemini 3 Flash Preview', description: '공식 모델 ID 예시 · 빠른 응답에 적합한 Flash 계열입니다.' },
      { value: 'gemini-2.5-pro', displayName: 'Gemini 2.5 Pro', description: 'Gemini 2.5 Pro 모델을 지정합니다.' },
      { value: 'gemini-2.5-flash', displayName: 'Gemini 2.5 Flash', description: 'Gemini 2.5 Flash 모델을 지정합니다.' },
    ],
  }
}

export function fallbackProviderRuntime(id: ProviderId, preview = false): ProviderRuntime {
  return {
    id, name: id === 'claude' ? 'Claude Code' : `${providerLabel(id)} CLI`, available: false,
    detail: preview ? '브라우저 미리보기에서는 로컬 CLI를 실행할 수 없습니다.' : `${providerLabel(id)} CLI 설치 상태를 확인해 주세요.`,
    modelCatalog: fallbackProviderCatalog(id, preview),
    capabilities: {
      effort: id !== 'gemini',
      permissionModes: id === 'codex' ? ['manual', 'acceptEdits'] : ['manual', 'plan', 'acceptEdits'],
      maxTurns: id === 'claude', maxBudgetUsd: id === 'claude', resume: true,
    },
  }
}

export function providerEffortLevels(id: ProviderId, model: string, catalog?: ClaudeModelCatalog): Exclude<ClaudeEffort, 'default'>[] {
  if (id === 'claude') return effortLevelsForModel(model, catalog)
  if (id === 'gemini') return []
  const option = catalog?.models.find((entry) => entry.value === model || entry.resolvedModel === model)
  if (option?.supportsEffort === false) return []
  if (option?.supportedEffortLevels) return option.supportedEffortLevels.filter((level) => EFFORT_LEVELS.includes(level))
  // A default or custom model can resolve through config or a resumed session.
  // Only advertise levels the installed CLI actually returned for that ID.
  return []
}

/** Restore old panes and reset options which the selected CLI cannot express. */
export function normalizeProviderSettings(id: ProviderId, value: unknown): ClaudeRunSettings {
  const settings = normalizeRunSettings(value)
  const capabilities = fallbackProviderRuntime(id).capabilities
  return {
    ...settings,
    effort: capabilities.effort ? settings.effort : 'default',
    permissionMode: capabilities.permissionModes.includes(settings.permissionMode) ? settings.permissionMode : 'manual',
    maxTurns: capabilities.maxTurns ? settings.maxTurns : null,
    maxBudgetUsd: capabilities.maxBudgetUsd ? settings.maxBudgetUsd : null,
  }
}
