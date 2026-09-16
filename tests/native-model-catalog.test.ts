import { describe, expect, it, vi } from 'vitest'
import { closeModelCatalogLookups, normalizeModelCatalog, readCliModelCatalog, type ModelQueryFactory } from '../electron/main/claude-models'
import { validateModelSelection, validateStartRequest } from '../electron/main/validation'
import { DEFAULT_RUN_SETTINGS } from '../shared/claude-options'

describe('Claude model catalog', () => {
  it('keeps CLI provider capabilities and descriptions while matching the app’s default-model meaning', () => {
    const catalog = normalizeModelCatalog([
      { value: 'default', displayName: 'Default (recommended)', description: 'Account default Opus', supportsEffort: true, supportedEffortLevels: ['high'] },
      { value: 'company/model@v2', resolvedModel: 'provider/company-model@v2', displayName: 'Company model', description: 'Available using usage credits.', supportsEffort: true, supportedEffortLevels: ['low', 'high', 'future-level'], secret: 'private data' },
      { value: 'haiku', displayName: 'Haiku', description: 'Fast' },
      { value: '--help', displayName: 'Unsafe', description: '' },
      { value: 'company/model@v2', displayName: 'Duplicate', description: '' },
    ])
    expect(catalog.source).toBe('cli')
    expect(catalog.models).toHaveLength(3)
    expect(catalog.models[0]).toMatchObject({ value: 'default', displayName: 'Claude 설정 따름' })
    expect(catalog.models[0]).not.toHaveProperty('supportsEffort')
    expect(catalog.models[0]).not.toHaveProperty('supportedEffortLevels')
    expect(catalog.models[1]).toEqual({ value: 'company/model@v2', resolvedModel: 'provider/company-model@v2', displayName: 'Company model', description: 'Available using usage credits.', supportsEffort: true, supportedEffortLevels: ['low', 'high'] })
    expect(catalog.models[2]).toMatchObject({ supportsEffort: false, supportedEffortLevels: [] })
    expect(JSON.stringify(catalog)).not.toContain('private data')
    const request = validateStartRequest({ sessionId: 'session-1', workspaceId: 'workspace-1', kind: 'claude', input: 'Fixture', model: 'provider/company-model@v2', settings: { ...DEFAULT_RUN_SETTINGS, effort: 'high' } })
    expect(() => validateModelSelection(request, catalog)).not.toThrow()
    expect(() => validateModelSelection({ ...request, settings: { ...DEFAULT_RUN_SETTINGS, effort: 'max' } }, catalog)).toThrow('지원하지 않습니다')
  })

  it('queries metadata with a prompt iterable that never yields a user message', async () => {
    let parameters: Parameters<ModelQueryFactory>[0] | undefined
    let pendingInput: Promise<IteratorResult<unknown>> | undefined
    const close = vi.fn()
    const factory: ModelQueryFactory = (received) => {
      parameters = received
      if (typeof received.prompt === 'string') throw new Error('A string prompt must never be used for metadata')
      pendingInput = received.prompt[Symbol.asyncIterator]().next()
      return { supportedModels: async () => [{ value: 'sonnet', displayName: 'Sonnet', description: 'Fixture', supportsEffort: true, supportedEffortLevels: ['low', 'medium', 'high'] }], close }
    }
    const catalog = await readCliModelCatalog('/installed/claude', factory)
    expect(catalog.source).toBe('cli')
    expect(close).toHaveBeenCalledOnce()
    await expect(pendingInput).resolves.toEqual({ done: true, value: undefined })
    expect(parameters?.options).toMatchObject({
      pathToClaudeCodeExecutable: '/installed/claude', persistSession: false,
      strictMcpConfig: true, mcpServers: {}, tools: [], permissionMode: 'dontAsk', permissionPrompts: 'none',
      extraArgs: { 'safe-mode': null }, env: { CLAUDE_CODE_SAFE_MODE: '1', DISABLE_AUTOUPDATER: '1', CLAUDE_CODE_SKIP_PROMPT_HISTORY: '1' },
    })
  })

  it('falls back after a bounded lookup timeout and closes the SDK transport', async () => {
    const close = vi.fn()
    const factory: ModelQueryFactory = () => ({ supportedModels: () => new Promise(() => undefined), close })
    const catalog = await readCliModelCatalog('/installed/claude', factory, 10)
    expect(catalog.source).toBe('fallback')
    expect(catalog.models.map((model) => model.value)).toContain('sonnet')
    expect(close).toHaveBeenCalledOnce()
  })

  it('cancels metadata lookup on app shutdown without waiting for the timeout', async () => {
    const close = vi.fn()
    const factory: ModelQueryFactory = () => ({ supportedModels: () => new Promise(() => undefined), close })
    const pending = readCliModelCatalog('/installed/claude', factory, 60_000)
    await closeModelCatalogLookups()
    expect((await pending).source).toBe('fallback')
    expect(close).toHaveBeenCalledOnce()
    const lateFactory = vi.fn(factory)
    expect((await readCliModelCatalog('/late/claude', lateFactory)).source).toBe('fallback')
    expect(lateFactory).not.toHaveBeenCalled()
  })
})
