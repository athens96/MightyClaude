import type { ClaudeEffort, ClaudeModelCatalog, ClaudeModelOption, ClaudeRunSettings } from './types'

export const EFFORT_LEVELS: Exclude<ClaudeEffort, 'default'>[] = ['low', 'medium', 'high', 'xhigh', 'max']
export const DEFAULT_RUN_SETTINGS: ClaudeRunSettings = {
  effort: 'default', permissionMode: 'manual', maxTurns: null, maxBudgetUsd: null,
}

export function isClaudeModel(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 200 && /^[a-zA-Z0-9][a-zA-Z0-9._:/@\[\]-]*$/.test(value)
}

/** Migrate older saved panes and discard corrupt values without losing the session. */
export function normalizeRunSettings(value: unknown): ClaudeRunSettings {
  const settings = value && typeof value === 'object' ? value as Record<string, unknown> : {}
  return {
    effort: EFFORT_LEVELS.includes(settings.effort as never) ? settings.effort as ClaudeEffort : 'default',
    permissionMode: settings.permissionMode === 'plan' || settings.permissionMode === 'acceptEdits' ? settings.permissionMode : 'manual',
    maxTurns: typeof settings.maxTurns === 'number' && Number.isInteger(settings.maxTurns) && settings.maxTurns >= 1 && settings.maxTurns <= 1000 ? settings.maxTurns : null,
    maxBudgetUsd: typeof settings.maxBudgetUsd === 'number' && Number.isFinite(settings.maxBudgetUsd) && settings.maxBudgetUsd > 0 && settings.maxBudgetUsd <= 10_000 ? settings.maxBudgetUsd : null,
  }
}

/** Official aliases are a fallback, not an assertion of account entitlement. */
export function fallbackModelCatalog(preview = false): ClaudeModelCatalog {
  return {
    source: preview ? 'preview' : 'fallback',
    detail: preview ? '미리보기 · Claude 공식 모델 별칭입니다.' : 'Claude 공식 모델 별칭 · 계정에서 사용 가능한 모델은 Claude Code 설정에 따라 달라집니다.',
    models: [
      { value: 'default', displayName: 'Claude 설정 따름', description: 'Claude Code의 모델 설정과 재개한 세션의 모델을 사용합니다.' },
      { value: 'best', displayName: 'Best', description: '계정에서 사용할 수 있는 최상위 모델을 Claude가 선택합니다.' },
      { value: 'fable', displayName: 'Fable', description: '연결된 제공자의 최신 Fable 모델입니다.' },
      { value: 'opus', displayName: 'Opus', description: '연결된 제공자의 최신 Opus 모델입니다.' },
      { value: 'sonnet', displayName: 'Sonnet', description: '연결된 제공자의 최신 Sonnet 모델입니다.' },
      { value: 'haiku', displayName: 'Haiku', description: '빠르고 가벼운 작업을 위한 Haiku 모델입니다.', supportsEffort: false, supportedEffortLevels: [] },
      { value: 'opusplan', displayName: 'Opus Plan', description: '계획은 Opus, 실행은 Sonnet으로 진행합니다.' },
    ],
  }
}

/** Known capabilities only; unknown provider models keep the CLI default effort. */
export function effortLevelsForModel(model: string, catalog?: ClaudeModelCatalog): Exclude<ClaudeEffort, 'default'>[] {
  const option: ClaudeModelOption | undefined = catalog?.models.find((entry) => entry.value === model)
    ?? catalog?.models.find((entry) => entry.resolvedModel === model)
  if (option?.supportsEffort === false) return []
  if (option?.supportedEffortLevels) return option.supportedEffortLevels.filter((level) => EFFORT_LEVELS.includes(level))
  if (/haiku/i.test(model)) return []
  if (/(?:opus|sonnet)[-.]4[-.]6/.test(model)) return EFFORT_LEVELS.filter((level) => level !== 'xhigh')
  if (/^(?:default|best|fable|opus|sonnet|opusplan)(?:\[1m\])?$/.test(model)) return [...EFFORT_LEVELS]
  if (/(?:fable[-.]5|opus[-.](?:5|4[-.][78])|sonnet[-.]5)/.test(model)) return [...EFFORT_LEVELS]
  return option?.supportsEffort === true ? ['low', 'medium', 'high'] : []
}
