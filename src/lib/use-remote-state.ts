import { useCallback, useEffect, useRef, useState } from 'react'
import type { RemoteState } from '../../shared/types'
import { bridge } from './browser-bridge'

export function useRemoteState(enabled: boolean, visible: boolean, activeConnectionId?: string) {
  const [state, setState] = useState<RemoteState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const pending = useRef(false)
  const refreshing = useRef(false)
  const generation = useRef(0)
  const alive = useRef(true)
  const probeConnectionId = state?.connections.some((connection) => connection.id === activeConnectionId && connection.status === 'connected') ? activeConnectionId : undefined
  useEffect(() => { alive.current = true; return () => { alive.current = false; generation.current += 1 } }, [])

  const refresh = useCallback(async (probeConnectionId?: string) => {
    if (!bridge.getRemoteState || pending.current || refreshing.current) return
    refreshing.current = true
    const current = ++generation.current
    try {
      const next = probeConnectionId && bridge.refreshRemote
        ? await bridge.refreshRemote(probeConnectionId)
        : await bridge.getRemoteState()
      if (alive.current && current === generation.current) { setState(next); setError(null) }
    } catch (refreshError) {
      if (!alive.current || current !== generation.current) return
      setError(refreshError instanceof Error ? refreshError.message : '원격 상태를 확인하지 못했습니다.')
      if (probeConnectionId) setState((previous) => previous ? {
        ...previous,
        connections: previous.connections.map((connection) => connection.id === probeConnectionId ? { ...connection, status: 'disconnected', detail: '원격 컴퓨터에 연결할 수 없습니다.' } : connection),
      } : previous)
    } finally { refreshing.current = false }
  }, [])

  const perform = useCallback(async (operation: () => Promise<RemoteState>) => {
    if (pending.current) return false
    pending.current = true
    generation.current += 1
    setBusy(true)
    setError(null)
    try {
      const next = await operation()
      if (alive.current) setState(next)
      return true
    } catch (operationError) {
      if (alive.current) setError(operationError instanceof Error ? operationError.message : '원격 작업을 완료하지 못했습니다.')
      return false
    } finally {
      generation.current += 1
      pending.current = false
      if (alive.current) setBusy(false)
    }
  }, [])

  useEffect(() => { if (enabled) void refresh() }, [enabled, refresh])
  useEffect(() => {
    if (!enabled || (!visible && !activeConnectionId && !state?.host.enabled)) return
    // A disconnected peer stays disconnected until the user explicitly refreshes it.
    const timer = setInterval(() => { void refresh(probeConnectionId) }, 10_000)
    return () => clearInterval(timer)
  }, [enabled, visible, activeConnectionId, probeConnectionId, state?.host.enabled, refresh])

  return { state, error, busy, refresh, perform }
}
