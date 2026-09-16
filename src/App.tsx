import { useCallback, useEffect, useRef, useState } from 'react'
import { ArrowRight, Check, ChevronRight, Columns2, FolderOpen, FolderPlus, Grid2X2, Info, LoaderCircle, Maximize, Minus, Monitor, PanelLeftClose, PanelLeftOpen, Plus, Settings2, Square, Terminal, TriangleAlert, X } from 'lucide-react'
import type { AppSnapshot, ClaudeModel, ClaudeRunSettings, LayoutMode, ProviderId, RunSession, RuntimeInfo, SessionKind } from '../shared/types'
import { EMPTY_SNAPSHOT } from '../shared/types'
import { DEFAULT_RUN_SETTINGS } from '../shared/claude-options'
import { normalizeProvider, normalizeProviderSettings, providerEffortLevels, providerLabel, PROVIDER_IDS } from '../shared/provider-options'
import { BrandMark } from './components/BrandMark'
import { Modal } from './components/Modal'
import { SessionPane } from './components/SessionPane'
import { Sidebar } from './components/Sidebar'
import { RemoteDialog } from './components/RemoteDialog'
import { bridge } from './lib/browser-bridge'
import { providerForRuntime } from './lib/providers'
import { useRemoteState } from './lib/use-remote-state'
import { addWorkspace, applyRunEvent, createSession, makeId, removeSession, removeWorkspace, restoreSnapshot, selectWorkspace } from './lib/state'

type Dialog = { type: 'settings' } | { type: 'remote' } | { type: 'workspace' } | { type: 'remove-workspace'; workspaceId: string } | null

const ERROR_FALLBACK = '작업을 완료하지 못했습니다. 다시 시도해 주세요.'
function errorMessage(error: unknown): string { return error instanceof Error ? error.message : ERROR_FALLBACK }

export default function App() {
  const [snapshot, setSnapshot] = useState<AppSnapshot>(EMPTY_SNAPSHOT)
  const [loaded, setLoaded] = useState(false)
  const [runtime, setRuntime] = useState<RuntimeInfo | null>(null)
  const [query, setQuery] = useState('')
  const [dialog, setDialog] = useState<Dialog>(null)
  const [error, setError] = useState<string | null>(null)
  const [sidebarHidden, setSidebarHidden] = useState(false)
  const [busy, setBusy] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [previewPath, setPreviewPath] = useState('')
  const [drafts, setDrafts] = useState<Record<string, string>>({})
  const searchRef = useRef<HTMLInputElement>(null)
  const snapshotRef = useRef(snapshot)
  const loadedRef = useRef(false)
  const saveQueue = useRef(Promise.resolve())
  const saveTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const flushing = useRef(false)
  const inFlight = useRef(new Set<string>())
  snapshotRef.current = snapshot
  const isMac = runtime?.platform === 'darwin' || (runtime?.platform === 'browser' && /Mac/i.test(navigator.platform))
  const activeWorkspace = snapshot.workspaces.find((workspace) => workspace.id === snapshot.activeWorkspaceId)
  const remote = useRemoteState(loaded, dialog?.type === 'remote', activeWorkspace?.remote?.connectionId)
  const activeConnection = remote.state?.connections.find((connection) => connection.id === activeWorkspace?.remote?.connectionId)
  const workspaceRuntime = activeWorkspace?.remote ? activeConnection?.runtime : runtime
  const sessions = snapshot.sessions.filter((session) => session.workspaceId === activeWorkspace?.id)
  const activeSession = sessions.find((session) => session.id === snapshot.activeSessionId) ?? sessions[0]
  const visibleSessions = snapshot.layout === 'focus' && activeSession ? [activeSession] : sessions
  const runningCount = sessions.filter((session) => session.status === 'running').length
  const activeProvider = providerForRuntime(workspaceRuntime, normalizeProvider(activeSession?.provider), !bridge.isNative)

  function runtimeForSession(session: RunSession) {
    const workspace = snapshotRef.current.workspaces.find((entry) => entry.id === session.workspaceId)
    const info = workspace?.remote ? remote.state?.connections.find((connection) => connection.id === workspace.remote?.connectionId)?.runtime : runtime
    return providerForRuntime(info, normalizeProvider(session.provider), !bridge.isNative)
  }

  function executionBlocked(session: RunSession): string | undefined {
    const workspace = snapshotRef.current.workspaces.find((entry) => entry.id === session.workspaceId)
    if (workspace?.remote) {
      const connection = remote.state?.connections.find((entry) => entry.id === workspace.remote?.connectionId)
      if (!connection || connection.status !== 'connected') return '원격 연결이 끊어졌습니다. 원격 연결에서 컴퓨터 상태를 새로고침하세요.'
      if (!connection.runtime) return '원격 컴퓨터의 실행 환경을 확인하고 있습니다.'
    }
    if (bridge.isNative && session.kind !== 'shell') {
      const info = runtimeForSession(session)
      if (!info.available) return `${workspace?.remote ? '원격 컴퓨터' : '이 컴퓨터'}에서 ${info.name}를 확인하세요. ${info.detail}`
    }
    return undefined
  }

  useEffect(() => {
    let disposed = false
    const unsubscribe = bridge.onRunEvent((event) => { if (!disposed) setSnapshot((current) => applyRunEvent(current, event)) })
    void bridge.loadState().then((saved) => {
      if (disposed) return
      setSnapshot(restoreSnapshot(saved))
      loadedRef.current = true
      setLoaded(true)
    }).catch((loadError: unknown) => {
      if (disposed) return
      setError(`작업 공간을 불러오지 못했습니다. ${errorMessage(loadError)}`)
      // Keep saving disabled after a failed read to preserve the user's stored workspaces.
    })
    void bridge.getRuntimeInfo().then((info) => { if (!disposed) setRuntime(info) }).catch((runtimeError: unknown) => { if (!disposed) setError(errorMessage(runtimeError)) })
    return () => { disposed = true; unsubscribe() }
  }, [])

  useEffect(() => { document.documentElement.dataset.theme = snapshot.theme }, [snapshot.theme])
  useEffect(() => {
    if (!loaded || flushing.current) return
    const timer = setTimeout(() => {
      saveTimer.current = null
      saveQueue.current = saveQueue.current.catch(() => {}).then(() => bridge.saveState(snapshot)).catch((saveError: unknown) => { setError(`저장하지 못했습니다. ${errorMessage(saveError)}`) })
    }, 160)
    saveTimer.current = timer
    return () => {
      clearTimeout(timer)
      if (saveTimer.current === timer) saveTimer.current = null
    }
  }, [loaded, snapshot])
  useEffect(() => {
    return bridge.onBeforeQuit?.(async () => {
      flushing.current = true
      if (saveTimer.current !== null) {
        clearTimeout(saveTimer.current)
        saveTimer.current = null
      }
      try {
        // Drain older writes first, then let the final process events reach React.
        await saveQueue.current
        await new Promise<void>((resolve) => setTimeout(resolve, 0))
        if (loadedRef.current) await bridge.saveState(snapshotRef.current)
      } catch (saveError) {
        setError(`종료 전 작업 공간을 저장하지 못했습니다. ${errorMessage(saveError)}`)
        throw saveError
      } finally {
        flushing.current = false
      }
    })
  }, [])
  useEffect(() => {
    const saveBeforeClose = () => {
      // Native quit waits for the acknowledged flush above; browser storage is synchronous.
      if (loadedRef.current && (!bridge.isNative || !bridge.onBeforeQuit)) void bridge.saveState(snapshotRef.current).catch(() => {})
    }
    window.addEventListener('beforeunload', saveBeforeClose)
    return () => window.removeEventListener('beforeunload', saveBeforeClose)
  }, [])

  const pickWorkspace = useCallback(async () => {
    if (!loaded || busy) return
    if (!bridge.isNative) { setPreviewPath(''); setDialog({ type: 'workspace' }); return }
    setBusy(true)
    try {
      const workspace = await bridge.pickWorkspace()
      if (workspace) { setSnapshot((current) => addWorkspace(current, workspace)); setQuery('') }
    } catch (pickError) { setError(errorMessage(pickError)) } finally { setBusy(false) }
  }, [busy, loaded])

  const newSession = useCallback((kind: SessionKind = 'claude') => {
    if (!loaded) return
    setSnapshot((current) => {
      if (!current.activeWorkspaceId) return current
      const session = createSession(current.activeWorkspaceId, kind, current.sessions)
      return { ...current, sessions: [...current.sessions, session], activeSessionId: session.id }
    })
    requestAnimationFrame(() => document.querySelector<HTMLTextAreaElement>('.session-pane.is-active textarea')?.focus())
  }, [loaded])

  useEffect(() => {
    const handleShortcut = (event: KeyboardEvent) => {
      if (event.defaultPrevented || !(event.metaKey || event.ctrlKey) || event.altKey || event.isComposing) return
      const key = event.key.toLowerCase()
      if (dialog || document.querySelector('[role="dialog"][aria-modal="true"]')) {
        if (key === 'o' || key === 'n' || key === 'k') event.preventDefault()
        return
      }
      if (key === 'o') { event.preventDefault(); void pickWorkspace() }
      if (key === 'n') { event.preventDefault(); newSession('claude') }
      if (key === 'k') {
        event.preventDefault()
        setSidebarHidden(false)
        requestAnimationFrame(() => { searchRef.current?.focus(); searchRef.current?.select() })
      }
    }
    window.addEventListener('keydown', handleShortcut)
    return () => window.removeEventListener('keydown', handleShortcut)
  }, [dialog, newSession, pickWorkspace])

  async function submitRun(session: RunSession, input: string) {
    if (inFlight.current.has(session.id)) return
    const blocked = executionBlocked(session)
    if (blocked) { setError(blocked); return }
    inFlight.current.add(session.id)
    const provider = normalizeProvider(session.provider)
    const settings = normalizeProviderSettings(provider, session.settings)
    const catalog = runtimeForSession(session).modelCatalog
    if (settings.effort !== 'default' && !providerEffortLevels(provider, session.model, catalog).includes(settings.effort)) settings.effort = 'default'
    setSnapshot((current) => applyRunEvent(applyRunEvent(current, { sessionId: session.id, type: 'log', entry: { id: makeId('log'), kind: 'user', text: input, timestamp: new Date().toISOString() } }), { sessionId: session.id, type: 'status', status: 'running' }))
    try {
      await bridge.startRun({ sessionId: session.id, workspaceId: session.workspaceId, kind: session.kind, input, model: session.model, provider, settings, resumeId: session.resumeId })
    } catch (runError) {
      setSnapshot((current) => applyRunEvent(applyRunEvent(current, { sessionId: session.id, type: 'log', entry: { id: makeId('log'), kind: 'error', text: errorMessage(runError), timestamp: new Date().toISOString() } }), { sessionId: session.id, type: 'status', status: 'error' }))
    } finally { inFlight.current.delete(session.id) }
  }

  async function stopRun(sessionId: string) {
    try { await bridge.stopRun(sessionId) } catch (stopError) { setError(errorMessage(stopError)) }
  }

  async function closeSession(session: RunSession) {
    try {
      if (session.status === 'running' || inFlight.current.has(session.id)) await bridge.stopRun(session.id)
      setSnapshot((current) => removeSession(current, session.id))
      setDrafts((current) => { const next = { ...current }; delete next[session.id]; return next })
    } catch (closeError) { setError(`실행 창을 닫지 못했습니다. ${errorMessage(closeError)}`) }
  }

  async function deleteWorkspace(workspaceId: string) {
    setBusy(true)
    try {
      const workspaceSessions = snapshotRef.current.sessions.filter((session) => session.workspaceId === workspaceId)
      const toStop = workspaceSessions.filter((session) => session.status === 'running' || inFlight.current.has(session.id))
      await Promise.all(toStop.map((session) => bridge.stopRun(session.id)))
      setSnapshot((current) => removeWorkspace(current, workspaceId))
      setDrafts((current) => { const next = { ...current }; for (const session of workspaceSessions) delete next[session.id]; return next })
      setDialog(null)
    } catch (deleteError) { setError(`워크스페이스를 제거하지 못했습니다. ${errorMessage(deleteError)}`) } finally { setBusy(false) }
  }

  function updateSession(sessionId: string, patch: Partial<Pick<RunSession, 'title'>>) {
    setSnapshot((current) => ({ ...current, sessions: current.sessions.map((session) => session.id === sessionId ? { ...session, ...patch } : session) }))
  }

  function changeModel(sessionId: string, model: ClaudeModel) {
    setSnapshot((current) => ({
      ...current,
      sessions: current.sessions.map((session) => {
        if (session.id !== sessionId || session.status === 'running' || inFlight.current.has(sessionId)) return session
        const provider = normalizeProvider(session.provider)
        const settings = normalizeProviderSettings(provider, session.settings)
        if (settings.effort !== 'default' && !providerEffortLevels(provider, model, runtimeForSession(session).modelCatalog).includes(settings.effort)) settings.effort = 'default'
        return { ...session, model, settings }
      }),
    }))
  }

  function updateRunSettings(sessionId: string, value: ClaudeRunSettings) {
    setSnapshot((current) => ({
      ...current,
      sessions: current.sessions.map((session) => {
        if (session.id !== sessionId || session.status === 'running' || inFlight.current.has(sessionId)) return session
        const provider = normalizeProvider(session.provider)
        const settings = normalizeProviderSettings(provider, value)
        if (settings.effort !== 'default' && !providerEffortLevels(provider, session.model, runtimeForSession(session).modelCatalog).includes(settings.effort)) settings.effort = 'default'
        return { ...session, settings }
      }),
    }))
  }

  function changeProvider(sessionId: string, provider: ProviderId) {
    setSnapshot((current) => {
      const session = current.sessions.find((entry) => entry.id === sessionId)
      if (!session || session.status === 'running' || inFlight.current.has(sessionId) || normalizeProvider(session.provider) === provider) return current
      const next = { ...current, sessions: current.sessions.map((entry) => entry.id === sessionId ? { ...entry, provider, model: 'default', settings: normalizeProviderSettings(provider, DEFAULT_RUN_SETTINGS), resumeId: undefined } : entry) }
      return applyRunEvent(next, { sessionId, type: 'log', entry: { id: makeId('log'), kind: 'system', timestamp: new Date().toISOString(), text: `${providerLabel(provider)}로 전환했습니다. 기존 기록은 유지되며 다음 요청은 새 대화에서 시작합니다.` } })
    })
  }

  async function importRemoteWorkspace(connectionId: string, workspaceId: string) {
    if (!bridge.importRemoteWorkspace) throw new Error('데스크톱 앱에서 원격 워크스페이스를 열 수 있습니다.')
    const workspace = await bridge.importRemoteWorkspace({ connectionId, workspaceId })
    setSnapshot((current) => addWorkspace(current, workspace))
    setQuery('')
    setDialog(null)
  }

  function focusSession(session: RunSession) {
    setSnapshot((current) => ({ ...selectWorkspace(current, session.workspaceId), activeSessionId: session.id, layout: current.layout === 'focus' && current.activeSessionId === session.id ? 'grid' : 'focus' }))
  }

  function selectSession(session: RunSession) {
    setSnapshot((current) => ({ ...selectWorkspace(current, session.workspaceId), activeSessionId: session.id }))
  }

  function resizeSidebar(event: React.PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return
    event.preventDefault()
    event.currentTarget.setPointerCapture(event.pointerId)
    const initialX = event.clientX
    const initialWidth = snapshot.sidebarWidth
    const target = event.currentTarget
    document.body.classList.add('is-resizing')
    const onMove = (move: PointerEvent) => setSnapshot((current) => ({ ...current, sidebarWidth: Math.min(380, Math.max(208, initialWidth + move.clientX - initialX)) }))
    const onEnd = () => {
      document.body.classList.remove('is-resizing')
      target.removeEventListener('pointermove', onMove)
      target.removeEventListener('pointerup', onEnd)
      target.removeEventListener('pointercancel', onEnd)
      target.removeEventListener('lostpointercapture', onEnd)
    }
    target.addEventListener('pointermove', onMove)
    target.addEventListener('pointerup', onEnd)
    target.addEventListener('pointercancel', onEnd)
    target.addEventListener('lostpointercapture', onEnd)
  }

  async function refreshRuntime() {
    setRefreshing(true)
    try {
      if (activeWorkspace?.remote) await remote.refresh(activeWorkspace.remote.connectionId)
      else setRuntime(await bridge.getRuntimeInfo())
    } catch (refreshError) { setError(errorMessage(refreshError)) } finally { setRefreshing(false) }
  }

  const runtimeLabel = activeWorkspace?.remote ? `${activeWorkspace.remote.hostName} · ${activeConnection?.status === 'connected' ? '원격 연결됨' : '연결 끊김'}` : !bridge.isNative ? '브라우저 미리보기' : !runtime ? '실행 환경 확인 중' : `${activeProvider.name} ${activeProvider.available ? '설치됨' : '확인 필요'}`
  const removeTarget = dialog?.type === 'remove-workspace' ? snapshot.workspaces.find((workspace) => workspace.id === dialog.workspaceId) : undefined

  return (
    <div className={`app ${bridge.isNative && isMac ? 'native-mac' : ''} ${sidebarHidden ? 'sidebar-is-hidden' : ''}`} style={{ '--sidebar-width': `${snapshot.sidebarWidth}px` } as React.CSSProperties}>
      {!sidebarHidden && <Sidebar snapshot={snapshot} query={query} setQuery={setQuery} onPickWorkspace={() => void pickWorkspace()} onSelectWorkspace={(id) => setSnapshot((current) => selectWorkspace(current, id))} onSelectSession={selectSession} onRemoveWorkspace={(id) => setDialog({ type: 'remove-workspace', workspaceId: id })} onNewSession={() => newSession('claude')} onSettings={() => setDialog({ type: 'settings' })} onRemote={() => setDialog({ type: 'remote' })} onTheme={() => setSnapshot((current) => ({ ...current, theme: current.theme === 'dark' ? 'light' : 'dark' }))} searchRef={searchRef} isMac={isMac} native={bridge.isNative} />}
      {!sidebarHidden && <div className="sidebar-resizer" role="separator" aria-label="사이드바 너비" aria-orientation="vertical" aria-valuenow={snapshot.sidebarWidth} aria-valuemin={208} aria-valuemax={380} tabIndex={0} onPointerDown={resizeSidebar} onKeyDown={(event) => { if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); setSnapshot((current) => ({ ...current, sidebarWidth: Math.min(380, Math.max(208, current.sidebarWidth + (event.key === 'ArrowLeft' ? -12 : 12))) })) } }} />}
      <main className="main-area">
        <header className="app-header">
          <button className="icon-button sidebar-toggle" onClick={() => setSidebarHidden(!sidebarHidden)} title={sidebarHidden ? '사이드바 열기' : '사이드바 접기'} aria-label={sidebarHidden ? '사이드바 열기' : '사이드바 접기'}>{sidebarHidden ? <PanelLeftOpen size={17} /> : <PanelLeftClose size={17} />}</button>
          <div className="workspace-heading"><div className="breadcrumb"><span>워크스페이스</span><ChevronRight size={12} /><strong>{activeWorkspace?.name ?? '시작하기'}</strong>{activeWorkspace?.remote && <span className="remote-workspace-badge">원격</span>}</div><p title={activeWorkspace?.path}>{activeWorkspace?.remote ? `${activeWorkspace.remote.hostName} · ${activeWorkspace.path}` : activeWorkspace?.path ?? '아이디어가 코드가 되는 공간'}</p></div>
          <button className={`runtime-badge ${!bridge.isNative ? 'preview-badge' : (activeWorkspace?.remote ? activeConnection?.status === 'connected' : activeProvider.available) ? 'ready-badge' : ''}`} onClick={() => setDialog({ type: activeWorkspace?.remote ? 'remote' : 'settings' })}><span className="runtime-indicator" />{runtimeLabel}{!bridge.isNative && <Info size={12} />}</button>
          {runtime?.platform === 'win32' && <div className="window-controls"><button aria-label="창 최소화" onClick={() => bridge.windowAction('minimize')}><Minus size={14} /></button><button aria-label="창 최대화 또는 복원" onClick={() => bridge.windowAction('maximize')}><Square size={11} /></button><button className="window-close" aria-label="앱 닫기" onClick={() => bridge.windowAction('close')}><X size={16} /></button></div>}
        </header>
        {!loaded ? <div className="loading-state">{error ? <><TriangleAlert size={26} /><h2>워크스페이스를 불러오지 못했습니다</h2><p>{error}</p><button className="primary-button" onClick={() => window.location.reload()}>다시 시도</button></> : <><LoaderCircle className="spin" size={25} /><p>작업 공간을 준비하고 있습니다.</p></>}</div> : activeWorkspace ? (
          <>
            <div className="workspace-toolbar">
              <div className="toolbar-title"><span>실행 창</span><span className="count-badge">{sessions.length}</span>{runningCount > 0 && <span className="running-count"><span />{runningCount}개 실행 중</span>}</div>
              <div className="toolbar-actions">
                <div className="layout-switch" role="group" aria-label="실행 창 배치">{([{ mode: 'grid', label: '그리드 보기', icon: Grid2X2 }, { mode: 'columns', label: '열 보기', icon: Columns2 }, { mode: 'focus', label: '집중 보기', icon: Maximize }] as const).map(({ mode, label, icon: Icon }) => <button key={mode} className={snapshot.layout === mode ? 'is-selected' : ''} aria-label={label} title={label} aria-pressed={snapshot.layout === mode} onClick={() => setSnapshot((current) => ({ ...current, layout: mode as LayoutMode }))}><Icon size={14} /></button>)}</div>
                <span className="toolbar-divider" />
                <button className="secondary-button new-terminal" onClick={() => newSession('shell')}><Terminal size={14} /><span>터미널</span></button>
                <button className="primary-button new-claude" onClick={() => newSession('claude')}><Plus size={15} /><span>새 Claude</span></button>
              </div>
            </div>
            <div className="workspace-content">
              {sessions.length === 0 ? <div className="empty-workspace"><span className="empty-claude-glyph">✳</span><h2>첫 실행 창을 열어 보세요</h2><p>같은 프로젝트에서 여러 AI 세션과 명령을 함께 실행하세요.</p><button className="primary-button" onClick={() => newSession('claude')}><Plus size={16} />새 Claude 시작</button></div> : <div className={`pane-grid layout-${snapshot.layout} ${sessions.length === 3 ? 'has-three' : ''} ${sessions.length === 1 ? 'has-one' : ''}`}>{visibleSessions.map((session) => <SessionPane key={session.id} session={session} input={drafts[session.id] ?? ''} setInput={(input) => setDrafts((current) => ({ ...current, [session.id]: input }))} active={session.id === activeSession?.id} focused={snapshot.layout === 'focus'} preview={!bridge.isNative} workspaceName={activeWorkspace.name} providerRuntime={runtimeForSession(session)} remoteHost={activeWorkspace.remote?.hostName} executionBlocked={executionBlocked(session)} onActivate={() => { if (snapshotRef.current.activeSessionId !== session.id) setSnapshot((current) => ({ ...current, activeSessionId: session.id })) }} onFocus={() => focusSession(session)} onClose={() => void closeSession(session)} onSubmit={(input) => submitRun(session, input)} onStop={() => void stopRun(session.id)} onProvider={(provider) => changeProvider(session.id, provider)} onModel={(model) => changeModel(session.id, model)} onSettings={(settings) => updateRunSettings(session.id, settings)} onRename={(title) => updateSession(session.id, { title })} />)}</div>}
            </div>
          </>
        ) : (
          <div className="welcome-screen"><div className="welcome-wordmark"><BrandMark size={48} /></div><span className="welcome-eyebrow">YOUR NEXT GREAT IDEA STARTS HERE</span><h1>작업에 집중할 수 있는<br /><span>나만의 개발 공간.</span></h1><p>프로젝트를 열고, Claude와 함께 만들어 보세요.<br />여러 작업을 하나의 워크스페이스에서 이어갑니다.</p><button className="primary-button welcome-open" disabled={busy} onClick={() => void pickWorkspace()}><FolderPlus size={17} />프로젝트 폴더 열기<ArrowRight size={16} /></button><span className="welcome-shortcut">{isMac ? '⌘' : 'Ctrl'} + O 로 빠르게 열기</span><div className="welcome-features"><span><FolderOpen size={16} />프로젝트별 워크스페이스</span><span><Grid2X2 size={16} />나란히 실행하는 Claude</span><span><Terminal size={16} />통합 명령 실행</span></div></div>
        )}
        <footer className="statusbar"><span className="statusbar-left"><span className="statusbar-mark"><BrandMark size={13} /></span>{activeWorkspace?.remote ? `${activeWorkspace.remote.hostName}에서 실행` : !bridge.isNative ? '미리보기 · 명령은 실행되지 않습니다' : 'AI CLI 워크스페이스'}</span><span className="statusbar-right">{sessions.length > 0 && <span>{sessions.length}개의 실행 창</span>}<span className="statusbar-divider" /><span>v{runtime?.appVersion ?? '0.1.0'}</span><button aria-label="실행 환경 설정 열기" title="실행 환경" onClick={() => setDialog({ type: 'settings' })}><Settings2 size={12} /></button></span></footer>
      </main>
      {error && loaded && <div className="toast" role="alert"><TriangleAlert size={16} /><span>{error}</span><button className="icon-button" aria-label="오류 알림 닫기" onClick={() => setError(null)}><X size={15} /></button></div>}
      {dialog?.type === 'remote' && <RemoteDialog state={remote.state} error={remote.error} busy={remote.busy} workspaces={snapshot.workspaces} perform={remote.perform} onRefresh={(connectionId) => void remote.refresh(connectionId)} onImport={importRemoteWorkspace} onClose={() => setDialog(null)} />}
      {dialog?.type === 'workspace' && <Modal title="미리보기 워크스페이스 추가" description="레이아웃을 살펴볼 수 있는 가상 워크스페이스입니다." onClose={() => setDialog(null)}><form onSubmit={(event) => { event.preventDefault(); const path = previewPath.trim(); if (!path) return; const name = path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || 'Workspace'; setSnapshot((current) => addWorkspace(current, { id: makeId('workspace'), name, path, createdAt: new Date().toISOString() })); setQuery(''); setDialog(null) }}><label className="form-label" htmlFor="preview-path">프로젝트 폴더 경로</label><input id="preview-path" className="form-input" data-autofocus value={previewPath} placeholder="예: ~/Projects/my-app" onChange={(event) => setPreviewPath(event.target.value)} required maxLength={1024} /><div className="form-note"><Info size={14} /><p>실제 폴더는 데스크톱 앱의 폴더 선택기로 연결합니다.</p></div><div className="modal-footer"><button className="secondary-button" type="button" onClick={() => setDialog(null)}>취소</button><button className="primary-button" disabled={!previewPath.trim()} type="submit"><Plus size={14} />워크스페이스 추가</button></div></form></Modal>}
      {dialog?.type === 'remove-workspace' && removeTarget && <Modal title="워크스페이스를 제거할까요?" description={`“${removeTarget.name}”의 실행 창과 기록이 이 앱에서 제거됩니다.`} onClose={() => { if (!busy) setDialog(null) }}><div className="form-note"><FolderOpen size={16} /><p>프로젝트 폴더와 파일은 그대로 유지됩니다. 실행 중인 작업은 중지됩니다.</p></div><div className="modal-footer"><button className="secondary-button" disabled={busy} onClick={() => setDialog(null)}>취소</button><button className="danger-button" disabled={busy} onClick={() => void deleteWorkspace(removeTarget.id)}>{busy ? '제거 중…' : '워크스페이스 제거'}</button></div></Modal>}
      {dialog?.type === 'settings' && <Modal title="설정" description="개발 환경과 화면을 내 작업 방식에 맞추세요." className="settings-modal" onClose={() => setDialog(null)}>
        <section className="settings-section"><h3><Monitor size={15} />화면</h3><div className="settings-row"><div><strong>테마</strong><p>워크스페이스 전체에 적용됩니다.</p></div><div className="segmented-control">{(['dark', 'light'] as const).map((theme) => <button className={snapshot.theme === theme ? 'is-selected' : ''} aria-pressed={snapshot.theme === theme} key={theme} onClick={() => setSnapshot((current) => ({ ...current, theme }))}>{theme === 'dark' ? '어둡게' : '밝게'}</button>)}</div></div></section>
        <section className="settings-section"><h3><Terminal size={15} />{activeWorkspace?.remote ? `${activeWorkspace.remote.hostName} 실행 환경` : '이 컴퓨터 실행 환경'}</h3><div className="runtime-details"><div><span>클라이언트</span><strong>{runtime?.platform === 'browser' ? '브라우저 미리보기' : runtime?.platform === 'darwin' ? 'macOS' : runtime?.platform === 'win32' ? 'Windows' : runtime?.platform ?? '확인 중'}</strong></div><div><span>MOD 호환 기준</span><strong>{workspaceRuntime?.mods?.minimumVersion ?? '확인 중'}</strong></div></div><div className="provider-runtime-list">{PROVIDER_IDS.map((provider) => { const info = providerForRuntime(workspaceRuntime, provider, !bridge.isNative); return <div key={provider}><div><strong>{info.name}</strong><span>{!bridge.isNative ? '미리보기' : info.available ? info.version ?? '준비됨' : info.version ? `호환 확인 · ${info.version}` : '설치 필요'}</span></div><p>{info.detail}</p></div> })}</div><button className="secondary-button refresh-runtime" disabled={refreshing} onClick={() => void refreshRuntime()}>{refreshing ? <LoaderCircle size={14} className="spin" /> : <Check size={14} />}{refreshing ? '확인 중…' : '실행 환경 다시 확인'}</button></section>
        <section className="settings-section"><h3>키보드 단축키</h3><div className="shortcut-list"><div><span>프로젝트 폴더 열기</span><kbd>{isMac ? '⌘' : 'Ctrl'} O</kbd></div><div><span>새 Claude 실행 창</span><kbd>{isMac ? '⌘' : 'Ctrl'} N</kbd></div><div><span>워크스페이스 검색</span><kbd>{isMac ? '⌘' : 'Ctrl'} K</kbd></div></div></section><div className="settings-about"><BrandMark size={20} /><span>MightyClaude <small>v{runtime?.appVersion ?? '0.1.0'}</small></span><span>Made for your next build.</span></div>
      </Modal>}
    </div>
  )
}
