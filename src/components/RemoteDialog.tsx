import { useEffect, useState } from 'react'
import { Check, Copy, Eye, EyeOff, FolderOpen, Info, Link2, LoaderCircle, Monitor, Network, RefreshCw, ShieldCheck, Unplug } from 'lucide-react'
import type { RemoteState, Workspace } from '../../shared/types'
import { PROVIDER_IDS } from '../../shared/provider-options'
import { bridge } from '../lib/browser-bridge'
import { providerForRuntime } from '../lib/providers'
import { Modal } from './Modal'

export function RemoteDialog({ state, error, busy, workspaces, perform, onRefresh, onImport, onClose }: {
  state: RemoteState | null
  error: string | null
  busy: boolean
  workspaces: Workspace[]
  perform: (operation: () => Promise<RemoteState>) => Promise<boolean>
  onRefresh: (connectionId?: string) => void
  onImport: (connectionId: string, workspaceId: string) => Promise<void>
  onClose: () => void
}) {
  const [selected, setSelected] = useState<string[]>(state?.host.workspaceIds ?? [])
  const [port, setPort] = useState(String(state?.host.port ?? 43137))
  const [name, setName] = useState('')
  const [address, setAddress] = useState('')
  const [token, setToken] = useState('')
  const [showToken, setShowToken] = useState(false)
  const [copied, setCopied] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [importing, setImporting] = useState<string | null>(null)
  const supported = bridge.isNative && Boolean(bridge.getRemoteState)
  const localWorkspaces = workspaces.filter((workspace) => !workspace.remote)
  useEffect(() => {
    if (state?.host.enabled) { setSelected(state.host.workspaceIds); setPort(String(state.host.port ?? 43137)) }
  }, [state?.host.enabled, state?.host.port, state?.host.workspaceIds])
  useEffect(() => {
    if (!copied) return
    const timer = setTimeout(() => setCopied(null), 2000)
    return () => clearTimeout(timer)
  }, [copied])
  useEffect(() => { setShowToken(false) }, [state?.host.token])

  async function copy(value: string, label: string) {
    try { await navigator.clipboard.writeText(value); setCopied(label) }
    catch { setFormError('클립보드에 복사하지 못했습니다. 값을 선택해 직접 복사해 주세요.') }
  }

  async function startSharing() {
    setFormError(null)
    const number = Number(port)
    if (!Number.isInteger(number) || number < 1024 || number > 65535) { setFormError('공유 포트는 1,024부터 65,535 사이의 정수여야 합니다.'); return }
    const ids = selected.filter((id) => localWorkspaces.some((workspace) => workspace.id === id))
    if (ids.length === 0) { setFormError('공유할 로컬 워크스페이스를 하나 이상 선택하세요.'); return }
    if (bridge.startSharing) await perform(() => bridge.startSharing!({ workspaceIds: ids, port: number }))
  }

  async function connect() {
    setFormError(null)
    if (!name.trim() || !address.trim() || !token.trim()) { setFormError('연결 이름, 원격 주소, 연결 키를 입력하세요.'); return }
    if (bridge.connectRemote && await perform(() => bridge.connectRemote!({ name: name.trim(), address: address.trim(), token: token.trim() }))) { setToken(''); setName(''); setAddress('') }
  }

  async function importWorkspace(connectionId: string, workspaceId: string) {
    setImporting(`${connectionId}:${workspaceId}`)
    setFormError(null)
    try { await onImport(connectionId, workspaceId) }
    catch (importError) { setFormError(importError instanceof Error ? importError.message : '워크스페이스를 열지 못했습니다.') }
    finally { setImporting(null) }
  }

  return (
    <Modal title="원격 연결" description="Tailscale로 연결한 컴퓨터에서 AI CLI와 명령을 실행합니다." className="remote-modal" onClose={onClose}>
      <div className={`tailscale-status ${state?.tailscale.available ? 'is-ready' : ''}`}><Network size={17} /><div><strong>{state?.tailscale.available ? state.tailscale.deviceName ?? 'Tailscale 연결됨' : supported ? 'Tailscale 확인' : '데스크톱 앱에서 사용 가능'}</strong><p>{supported ? state?.tailscale.detail ?? '연결 상태를 확인하고 있습니다.' : '원격 공유와 연결은 Windows 또는 macOS 데스크톱 앱에서 사용할 수 있습니다.'}</p></div><button className="icon-button" aria-label="Tailscale 상태 새로고침" disabled={!supported || busy} onClick={() => onRefresh()}><RefreshCw size={14} /></button></div>
      {(error || formError) && <div className="remote-error" role="alert">{formError ?? error}</div>}
      <div className="remote-setup-grid">
        <section className="remote-section" aria-labelledby="share-computer-heading">
          <h3 id="share-computer-heading"><Monitor size={16} />이 컴퓨터 공유</h3>
          <p className="remote-section-description">선택한 프로젝트만 다른 컴퓨터에서 열 수 있습니다.</p>
          <div className="share-workspace-list" role="group" aria-label="공유할 워크스페이스">
            {localWorkspaces.length === 0 && <p className="field-description">먼저 로컬 프로젝트 폴더를 열어 주세요.</p>}
            {localWorkspaces.map((workspace) => <label className="share-workspace" key={workspace.id}><input type="checkbox" aria-label={`${workspace.name} 공유`} disabled={!supported || busy || state?.host.enabled} checked={selected.includes(workspace.id)} onChange={(event) => setSelected((current) => event.target.checked ? [...current, workspace.id] : current.filter((id) => id !== workspace.id))} /><span><strong>{workspace.name}</strong><small title={workspace.path}>{workspace.path}</small></span></label>)}
          </div>
          <label className="form-label" htmlFor="share-port">공유 포트</label><input className="form-input" id="share-port" inputMode="numeric" value={port} disabled={!supported || busy || state?.host.enabled} onChange={(event) => setPort(event.target.value)} />
          {state?.host.enabled ? <div className="sharing-details"><div className="sharing-heading"><span className="online-dot" />공유 중<span>{state.host.activeRuns}개 실행 중</span></div><label className="form-label" htmlFor="share-address">공유 주소</label><div className="secret-input-row"><input id="share-address" className="form-input" readOnly value={state.host.address ?? ''} /><button className="icon-button" aria-label="공유 주소 복사" disabled={!state.host.address} onClick={() => void copy(state.host.address ?? '', 'address')}>{copied === 'address' ? <Check size={14} /> : <Copy size={14} />}</button></div><label className="form-label" htmlFor="share-token">공유 연결 키</label><div className="secret-input-row"><input id="share-token" className="form-input" readOnly type={showToken ? 'text' : 'password'} autoComplete="off" value={state.host.token ?? ''} /><button className="icon-button" aria-label={showToken ? '연결 키 숨기기' : '연결 키 보기'} onClick={() => setShowToken(!showToken)}>{showToken ? <EyeOff size={14} /> : <Eye size={14} />}</button><button className="icon-button" aria-label="공유 연결 키 복사" disabled={!state.host.token} onClick={() => void copy(state.host.token ?? '', 'token')}>{copied === 'token' ? <Check size={14} /> : <Copy size={14} />}</button></div><p className="field-description">연결 키를 가진 컴퓨터는 선택한 프로젝트에서 명령을 실행할 수 있습니다.</p><button className="secondary-button remote-action" disabled={busy || !bridge.stopSharing} onClick={() => bridge.stopSharing && void perform(() => bridge.stopSharing!())}><Unplug size={14} />공유 중지</button></div> : <><button className="primary-button remote-action" disabled={!supported || busy || !bridge.startSharing || !state?.tailscale.available || selected.length === 0} onClick={() => void startSharing()}><Network size={14} />공유 시작</button>{state?.host.detail && <p className="field-description">{state.host.detail}</p>}</>}
        </section>
        <section className="remote-section" aria-labelledby="connect-computer-heading">
          <h3 id="connect-computer-heading"><Link2 size={16} />다른 컴퓨터 연결</h3>
          <p className="remote-section-description">공유한 컴퓨터의 Tailscale 주소와 연결 키를 입력하세요.</p>
          <form onSubmit={(event) => { event.preventDefault(); void connect() }}><fieldset className="remote-connect-fields" disabled={!supported || busy || !bridge.connectRemote}><label className="form-label" htmlFor="remote-name">연결 이름</label><input className="form-input" id="remote-name" placeholder="예: 작업실 Mac" value={name} maxLength={80} onChange={(event) => setName(event.target.value)} /><label className="form-label" htmlFor="remote-address">원격 주소</label><input className="form-input" id="remote-address" placeholder="http://100.x.x.x:43137" value={address} autoComplete="off" spellCheck={false} onChange={(event) => setAddress(event.target.value)} /><label className="form-label" htmlFor="remote-token">연결 키</label><input className="form-input" id="remote-token" type="password" value={token} autoComplete="off" onChange={(event) => setToken(event.target.value)} /><button className="primary-button remote-action" type="submit" disabled={!name.trim() || !address.trim() || !token.trim()}>{busy ? <LoaderCircle size={14} className="spin" /> : <Link2 size={14} />}컴퓨터 연결</button></fieldset></form>
          <div className="remote-identity-note"><ShieldCheck size={15} /><p>원격 컴퓨터에 설치하고 로그인한 CLI를 사용합니다. 이 컴퓨터의 로그인 정보는 전달하지 않습니다.</p></div>
        </section>
      </div>
      <section className="remote-connections" aria-label="연결된 컴퓨터"><h3>연결된 컴퓨터 <span>{state?.connections.length ?? 0}</span></h3>{!state?.connections.length && <p className="field-description">아직 연결한 컴퓨터가 없습니다.</p>}{state?.connections.map((connection) => <article className="remote-connection" key={connection.id}>
        <header><Monitor size={16} /><div><strong>{connection.name}</strong><small>{connection.hostName ?? connection.address}</small></div><span className={`connection-state ${connection.status}`}>{connection.status === 'connected' ? '연결됨' : '연결 끊김'}</span><button className="icon-button" aria-label={`${connection.name} 새로고침`} title="연결 새로고침" disabled={busy || !bridge.refreshRemote} onClick={() => onRefresh(connection.id)}><RefreshCw size={14} /></button><button className="icon-button" aria-label={`${connection.name} 연결 해제`} title="연결 해제" disabled={busy || !bridge.disconnectRemote} onClick={() => bridge.disconnectRemote && void perform(() => bridge.disconnectRemote!(connection.id))}><Unplug size={14} /></button></header>
        {connection.detail && <p className="remote-connection-detail">{connection.detail}</p>}
        <div className="remote-provider-list">{PROVIDER_IDS.map((provider) => { const info = providerForRuntime(connection.runtime, provider); return <div key={provider} title={info.detail}><span className={info.available && connection.status === 'connected' ? 'online-dot' : 'offline-dot'} /><strong>{info.name}</strong><span>{connection.status !== 'connected' ? '확인 불가' : !connection.runtime ? '환경 확인 중' : info.available ? info.version ?? '설치됨' : '설치 필요'}</span></div> })}</div>
        <div className="remote-workspaces">{connection.workspaces.length === 0 ? <p className="field-description">공유된 워크스페이스가 없습니다.</p> : connection.workspaces.map((workspace) => <div className="remote-workspace" key={workspace.id}><FolderOpen size={15} /><div><strong>{workspace.name}</strong><small title={workspace.path}>{workspace.path}</small></div><button className="secondary-button" aria-label={`${workspace.name} 원격 워크스페이스 열기`} disabled={busy || importing !== null || connection.status !== 'connected' || !bridge.importRemoteWorkspace} onClick={() => void importWorkspace(connection.id, workspace.id)}>{importing === `${connection.id}:${workspace.id}` ? '여는 중…' : '열기'}</button></div>)}</div>
      </article>)}</section>
      <div className="remote-footer-note"><Info size={13} /><span>공유는 ‘공유 시작’을 누른 뒤 켜집니다. 실행 위치는 워크스페이스마다 표시됩니다.</span></div>
    </Modal>
  )
}
