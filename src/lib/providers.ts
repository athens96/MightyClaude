import type { ProviderId, ProviderRuntime, RuntimeInfo } from '../../shared/types'
import { fallbackProviderRuntime } from '../../shared/provider-options'

export function providerForRuntime(runtime: RuntimeInfo | null | undefined, provider: ProviderId, preview = false): ProviderRuntime {
  const installed = runtime?.providers?.find((entry) => entry.id === provider)
  if (installed) return installed
  const fallback = fallbackProviderRuntime(provider, preview)
  if (provider !== 'claude' || !runtime) return fallback
  return {
    ...fallback,
    available: runtime.claudeAvailable,
    version: runtime.claudeVersion,
    modelCatalog: runtime.modelCatalog ?? fallback.modelCatalog,
    detail: runtime.mods?.detail ?? fallback.detail,
  }
}
