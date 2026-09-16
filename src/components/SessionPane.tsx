import { useEffect, useRef, useState } from 'react'
import { ArrowUp, Bot, Check, ChevronDown, CornerDownLeft, Copy, Gauge, Maximize2, Minimize2, SlidersHorizontal, Sparkles, Square, Terminal, X } from 'lucide-react'
import type { ClaudeEffort, ClaudeModel, ClaudeRunSettings, ProviderId, ProviderRuntime, RunSession } from '../../shared/types'
import { fallbackProviderCatalog, normalizeProvider, normalizeProviderSettings, providerEffortLevels, providerLabel, PROVIDER_IDS } from '../../shared/provider-options'
import { RunSettingsDialog } from './RunSettingsDialog'

const STATUS_LABELS = { idle: '준비됨', running: '실행 중', completed: '완료', stopped: '중지됨', error: '오류' }
const EFFORT_LABELS: Record<ClaudeEffort, string> = { default: 'Auto', low: 'Low', medium: 'Medium', high: 'High', xhigh: 'XHigh', max: 'Max' }

export function SessionPane({ session, input, setInput, active, focused, preview, workspaceName, providerRuntime, remoteHost, executionBlocked, onActivate, onFocus, onClose, onSubmit, onStop, onProvider, onModel, onSettings, onRename }: {
  session: RunSession
  input: string
  setInput: (input: string) => void
  active: boolean
  focused: boolean
  preview: boolean
  workspaceName: string
  providerRuntime: ProviderRuntime
  remoteHost?: string
  executionBlocked?: string
  onActivate: () => void
  onFocus: () => void
  onClose: () => void
  onSubmit: (input: string) => Promise<void>
  onStop: () => void
  onProvider: (provider: ProviderId) => void
  onModel: (model: ClaudeModel) => void
  onSettings: (settings: ClaudeRunSettings) => void
  onRename: (title: string) => void
}) {
  const [editing, setEditing] = useState(false)
  const [title, setTitle] = useState(session.title)
  const [copied, setCopied] = useState(false)
  const [copyError, setCopyError] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [settingsOpen, setSettingsOpen] = useState(false)
  const outputRef = useRef<HTMLDivElement>(null)
  const paneRef = useRef<HTMLElement>(null)
  const composerRef = useRef<HTMLTextAreaElement>(null)
  const atBottom = useRef(true)
  const composing = useRef(false)
  const copyTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const isShell = session.kind === 'shell'
  const running = session.status === 'running'
  const provider = normalizeProvider(session.provider)
  const providerName = providerLabel(provider)
  const modelCatalog = providerRuntime.modelCatalog
  const settings = normalizeProviderSettings(provider, session.settings)
  const models = modelCatalog.models.some((model) => model.value === 'default')
    ? modelCatalog.models
    : [fallbackProviderCatalog(provider).models[0], ...modelCatalog.models]
  const listedModel = models.find((model) => model.value === session.model)
  const selectedModel = listedModel ?? { value: session.model, displayName: `${session.model} · 저장된 모델`, description: '이 실행 창에 저장된 모델 ID를 그대로 사용합니다.' }
  const modelOptions = listedModel ? models : [...models, selectedModel]
  const efforts = providerRuntime.capabilities.effort ? providerEffortLevels(provider, session.model, modelCatalog) : []
  const displayedEffort = settings.effort === 'default' || efforts.includes(settings.effort) ? settings.effort : 'default'
  const customSettings = settings.effort !== 'default' || settings.permissionMode !== 'manual' || settings.maxTurns !== null || settings.maxBudgetUsd !== null
  const settingsSummary = [
    settings.permissionMode === 'plan' ? '계획만 진행' : settings.permissionMode === 'acceptEdits' ? '파일 수정 허용' : '기본 권한 사용',
    settings.maxTurns === null ? '턴 제한 없음' : `최대 ${settings.maxTurns}턴`,
    settings.maxBudgetUsd === null ? '비용 제한 없음' : `최대 $${settings.maxBudgetUsd}`,
  ].join(' · ')
  useEffect(() => { if (active) paneRef.current?.scrollIntoView({ block: 'nearest', inline: 'nearest' }) }, [active])
  useEffect(() => {
    if (atBottom.current && outputRef.current) outputRef.current.scrollTop = outputRef.current.scrollHeight
  }, [session.logs])
  useEffect(() => () => { if (copyTimer.current) clearTimeout(copyTimer.current) }, [])
  async function submit() {
    const value = input.trim()
    if (!value || running || submitting || executionBlocked) return
    setSubmitting(true)
    setInput('')
    atBottom.current = true
    try { await onSubmit(value) } finally { setSubmitting(false); composerRef.current?.focus() }
  }
  async function copyOutput() {
    try {
      await navigator.clipboard.writeText(session.logs.map((entry) => entry.text).join('\n\n'))
      setCopied(true)
      setCopyError(false)
    } catch { setCopyError(true) }
    if (copyTimer.current) clearTimeout(copyTimer.current)
    copyTimer.current = setTimeout(() => { setCopied(false); setCopyError(false) }, 2000)
  }
  function commitTitle() { onRename(title.trim() || session.title); setEditing(false) }

  return (
    <section ref={paneRef} className={`session-pane ${active ? 'is-active' : ''} ${isShell ? 'shell-pane' : `claude-pane provider-${provider}`}`} aria-label={`${session.title} 실행 창`} onMouseDown={onActivate} onFocusCapture={onActivate}>
      <header className="pane-header">
        <span className={`pane-kind-icon ${isShell ? 'shell-icon' : ''}`}>{isShell ? <Terminal size={15} /> : provider === 'codex' ? <Bot size={15} /> : provider === 'gemini' ? <Sparkles size={15} /> : <span className="claude-glyph" aria-hidden="true">✳</span>}</span>
        {editing ? <input className="pane-title-input" aria-label="실행 창 이름" value={title} maxLength={60} autoFocus onChange={(event) => setTitle(event.target.value)} onBlur={commitTitle} onKeyDown={(event) => { if (event.nativeEvent.isComposing) return; if (event.key === 'Enter') commitTitle(); if (event.key === 'Escape') { setTitle(session.title); setEditing(false) } }} /> : <button className="pane-title" title="실행 창 이름 바꾸기" onClick={() => { setTitle(session.title); setEditing(true) }}>{session.title}</button>}
        <span className={`pane-status status-${session.status}`}><span />{STATUS_LABELS[session.status]}</span>
        <div className="pane-actions">
          <button className="icon-button" title={focused ? '전체 창 보기' : '이 창에 집중'} aria-label={focused ? '전체 창 보기' : `${session.title} 창 확대`} onClick={onFocus}>{focused ? <Minimize2 size={13} /> : <Maximize2 size={13} />}</button>
          <button className="icon-button" title="실행 창 닫기" aria-label={`${session.title} 창 닫기`} onClick={onClose}><X size={14} /></button>
        </div>
      </header>
      <div className="pane-context">{isShell ? <span>{remoteHost ? 'REMOTE SHELL' : 'LOCAL SHELL'}</span> : <label className="provider-picker"><select aria-label={`${session.title} 실행기`} value={provider} disabled={running || submitting} onChange={(event) => onProvider(normalizeProvider(event.target.value))}>{PROVIDER_IDS.map((id) => <option key={id} value={id}>{id === 'claude' ? 'Claude Code' : `${providerLabel(id)} CLI`}</option>)}</select><ChevronDown size={10} /></label>}<span className="context-separator">/</span><span className="pane-context-name">{workspaceName}</span>{remoteHost && <span className="pane-remote-host" title={`${remoteHost}에서 실행`}>{remoteHost} · 원격</span>}{isShell && <span className="shell-limit">명령마다 새 셸 · 대화형 프로그램 미지원</span>}{preview && <span className="pane-preview">미리보기</span>}</div>
      {executionBlocked && <div className="pane-execution-notice" role="status">{executionBlocked}</div>}
      <div ref={outputRef} className="pane-output" role="log" aria-label={`${session.title} 출력`} aria-live="polite" aria-relevant="additions text" onScroll={() => { const node = outputRef.current; if (node) atBottom.current = node.scrollHeight - node.scrollTop - node.clientHeight < 80 }}>
        {session.logs.length === 0 ? (
          <div className={`pane-empty ${isShell ? 'shell-empty' : ''}`}>
            {isShell ? <div className="terminal-empty-icon"><Terminal size={24} strokeWidth={1.4} /></div> : provider === 'codex' ? <Bot className="empty-provider-icon" size={36} strokeWidth={1.3} /> : provider === 'gemini' ? <Sparkles className="empty-provider-icon" size={36} strokeWidth={1.3} /> : <span className="empty-claude-glyph" aria-hidden="true">✳</span>}
            <h2>{isShell ? '작업 폴더에서 명령 실행' : '다음 아이디어를 실현하세요.'}</h2>
            <p>{isShell ? '작업 폴더에서 명령을 실행하고\n결과를 바로 확인할 수 있습니다.' : `코드를 이해하고, 함께 설계하고, 만들어 보세요.\n이 창에서 독립적인 ${providerName} 세션을 시작합니다.`}</p>
            {!isShell && <div className="suggestion-chips"><button onClick={() => { setInput('이 프로젝트의 구조를 분석해 줘'); composerRef.current?.focus() }}>프로젝트 살펴보기 <span>↗</span></button><button onClick={() => { setInput('새로운 기능의 구현 계획을 함께 세워 줘'); composerRef.current?.focus() }}>구현 계획 세우기 <span>↗</span></button></div>}
            {isShell && <code className="shell-example"><span>$</span> {preview ? '명령을 입력해 보세요' : '작업 폴더에서 실행됩니다'}</code>}
          </div>
        ) : session.logs.map((entry) => (
          <div key={entry.id} className={`log-entry log-${entry.kind}`}>
            {entry.kind === 'user' && <span className="log-role">{isShell ? '$' : '나'}</span>}
            {entry.kind === 'assistant' && <span className="log-role claude-log-role">{entry.provider === 'codex' ? <Bot size={12} /> : entry.provider === 'gemini' ? <Sparkles size={12} /> : '✳'} {providerLabel(normalizeProvider(entry.provider))}</span>}
            {entry.kind === 'system' && <span className="log-role">시스템</span>}
            {entry.kind === 'error' && <span className="log-role">오류</span>}
            <pre>{entry.text}</pre>
          </div>
        ))}
        {running && <div className="running-indicator"><span /><span /><span /><span className="running-text">{isShell ? '명령 실행 중' : `${providerName} 작업 중`}</span></div>}
      </div>
      {session.logs.length > 0 && <div className="output-actions"><button className="text-button" onClick={() => void copyOutput()}>{copied ? <Check size={12} /> : <Copy size={12} />}{copyError ? '복사하지 못했습니다' : copied ? '복사됨' : '출력 복사'}</button><span>{session.logs.length}개 기록</span></div>}
      <form className={`composer ${isShell ? 'shell-composer' : ''}`} onSubmit={(event) => { event.preventDefault(); void submit() }}>
        <div className="composer-input-row">{isShell && <span className="shell-prompt">›</span>}<textarea ref={composerRef} aria-label={isShell ? `${session.title} 명령 입력` : `${session.title} 메시지 입력`} placeholder={isShell ? '명령을 입력하세요…' : `${providerName}에게 작업을 요청하세요…`} value={input} rows={isShell ? 1 : 2} onChange={(event) => setInput(event.target.value)} onCompositionStart={() => { composing.current = true }} onCompositionEnd={() => { composing.current = false }} onKeyDown={(event) => { if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing && !composing.current) { event.preventDefault(); void submit() } }} /></div>
        <div className="composer-toolbar">
          {!isShell ? <>
            <label className="model-picker" title={`${selectedModel.displayName}\n${selectedModel.description}`}>{provider === 'claude' ? <span className="claude-glyph" aria-hidden="true">✳</span> : provider === 'codex' ? <Bot size={13} /> : <Sparkles size={13} />}<select aria-label={`${session.title} 모델`} value={session.model} disabled={running || submitting} onChange={(event) => onModel(event.target.value)}>{modelOptions.map((model) => <option key={model.value} value={model.value} title={model.description}>{model.displayName}</option>)}</select><ChevronDown size={11} /></label>
            <label className={`effort-picker ${efforts.length === 0 ? 'is-unavailable' : ''}`} title={efforts.length === 0 ? `${providerName}의 이 모델은 CLI 기본 사고 강도를 사용합니다.` : '사고 강도 · Auto는 CLI 기본값을 사용합니다.'}><Gauge size={12} /><select aria-label={`${session.title} 사고 강도`} value={displayedEffort} disabled={running || submitting || efforts.length === 0} onChange={(event) => onSettings({ ...settings, effort: event.target.value as ClaudeEffort })}><option value="default">Auto</option>{efforts.map((effort) => <option key={effort} value={effort}>{EFFORT_LABELS[effort]}</option>)}</select><ChevronDown size={10} /></label>
            <button type="button" className={`icon-button run-settings-button ${customSettings ? 'has-custom-settings' : ''}`} title={settingsSummary} aria-label={`${session.title} 실행 설정`} aria-haspopup="dialog" aria-expanded={settingsOpen} disabled={running || submitting} onClick={() => setSettingsOpen(true)}><SlidersHorizontal size={14} />{customSettings && <span className="settings-active-dot" aria-hidden="true" />}</button>
          </> : <span className="shell-command-label">명령 실행</span>}
          <span className="composer-keyhint">{isShell ? <CornerDownLeft size={11} /> : '⇧ Enter 줄바꿈'}</span>
          {running ? <button type="button" className="send-button stop-button" title="실행 중지" aria-label={`${session.title} 실행 중지`} onClick={onStop}><Square size={12} fill="currentColor" /></button> : <button type="submit" className="send-button" title={executionBlocked ?? (isShell ? '명령 실행' : '메시지 보내기')} aria-label={isShell ? `${session.title} 명령 실행` : `${session.title} 메시지 보내기`} disabled={!input.trim() || submitting || Boolean(executionBlocked)}><ArrowUp size={17} /></button>}
        </div>
      </form>
      {settingsOpen && <RunSettingsDialog sessionTitle={session.title} settings={settings} selectedModel={selectedModel} catalog={modelCatalog} providerRuntime={providerRuntime} remoteHost={remoteHost} unavailableModel={!listedModel} running={running || submitting} onSave={onSettings} onClose={() => setSettingsOpen(false)} />}
    </section>
  )
}
