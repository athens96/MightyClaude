import { useRef } from 'react'
import { ChevronDown, Circle, Folder, FolderOpen, FolderPlus, Search, Settings2, Sun, Moon, Terminal, Trash2, Plus, Command, Network, Monitor, Bot, Sparkles } from 'lucide-react'
import type { AppSnapshot, RunSession } from '../../shared/types'
import { BrandMark } from './BrandMark'

export function Sidebar({ snapshot, query, setQuery, onPickWorkspace, onSelectWorkspace, onSelectSession, onRemoveWorkspace, onNewSession, onSettings, onRemote, onTheme, searchRef, isMac, native }: {
  snapshot: AppSnapshot
  query: string
  setQuery: (value: string) => void
  onPickWorkspace: () => void
  onSelectWorkspace: (id: string) => void
  onSelectSession: (session: RunSession) => void
  onRemoveWorkspace: (id: string) => void
  onNewSession: () => void
  onSettings: () => void
  onRemote: () => void
  onTheme: () => void
  searchRef: React.RefObject<HTMLInputElement | null>
  isMac: boolean
  native: boolean
}) {
  const composing = useRef(false)
  const workspaces = snapshot.workspaces.filter((workspace) => `${workspace.name} ${workspace.path}`.toLocaleLowerCase().includes(query.toLocaleLowerCase()))
  return (
    <aside className="sidebar" aria-label="워크스페이스">
      <div className={`sidebar-brand ${native && isMac ? 'mac-titlebar' : ''}`}>
        <div className="brand-symbol"><BrandMark size={25} /></div>
        <span>MightyClaude</span>
        <span className="brand-tag">ADE</span>
      </div>
      <div className="sidebar-tools">
        <label className="workspace-search">
          <Search size={14} />
          <input ref={searchRef} type="search" placeholder="워크스페이스 찾기" aria-label="워크스페이스 찾기" value={query} onChange={(event) => setQuery(event.target.value)} onCompositionStart={() => { composing.current = true }} onCompositionEnd={() => { composing.current = false }} onKeyDown={(event) => { if (event.key === 'Escape' && !composing.current) { setQuery(''); event.currentTarget.blur() } }} />
          {!query && <kbd>{isMac ? '⌘' : '⌃'} K</kbd>}
        </label>
        <button className="open-workspace-button" onClick={onPickWorkspace}><FolderPlus size={16} /><span>폴더 열기</span><kbd>{isMac ? '⌘' : '⌃'} O</kbd></button>
      </div>
      <div className="sidebar-section-heading"><span>워크스페이스</span><span>{snapshot.workspaces.length.toString().padStart(2, '0')}</span></div>
      <nav className="workspace-list" aria-label="워크스페이스 목록">
        {workspaces.length === 0 && <div className="sidebar-empty">{query ? '검색 결과가 없습니다.' : '폴더를 열고 작업을 시작하세요.'}</div>}
        {workspaces.map((workspace) => {
          const active = workspace.id === snapshot.activeWorkspaceId
          const sessions = snapshot.sessions.filter((session) => session.workspaceId === workspace.id)
          const running = sessions.filter((session) => session.status === 'running').length
          return (
            <div key={workspace.id} className={`workspace-group ${active ? 'is-active' : ''}`}>
              <div className="workspace-row">
                <button className="workspace-select" onClick={() => onSelectWorkspace(workspace.id)} aria-label={workspace.name} aria-current={active ? 'page' : undefined} title={workspace.remote ? `${workspace.remote.hostName} · ${workspace.path}` : workspace.path}>
                  <ChevronDown size={12} className={active ? '' : 'collapsed-chevron'} />
                  {workspace.remote ? <Monitor size={16} /> : active ? <FolderOpen size={16} /> : <Folder size={16} />}
                  <span>{workspace.name}</span>
                  {running > 0 && <span className="workspace-running" aria-label={`${running}개 실행 중`}>{running}</span>}
                </button>
                <button className="icon-button workspace-remove" title="워크스페이스 목록에서 제거" aria-label={`${workspace.name} 워크스페이스 제거`} onClick={() => onRemoveWorkspace(workspace.id)}><Trash2 size={12} /></button>
              </div>
              {workspace.remote && <div className="sidebar-remote-host"><Network size={10} /><span>{workspace.remote.hostName}</span></div>}
              {active && (
                <div className="workspace-sessions">
                  {sessions.map((session) => (
                    <button className={`session-nav ${snapshot.activeSessionId === session.id ? 'is-selected' : ''}`} key={session.id} onClick={() => onSelectSession(session)} aria-current={snapshot.activeSessionId === session.id ? 'true' : undefined}>
                      {session.kind === 'shell' ? <Terminal size={13} /> : session.provider === 'codex' ? <Bot size={13} /> : session.provider === 'gemini' ? <Sparkles size={13} /> : <span className="claude-glyph" aria-hidden="true">✳</span>}
                      <span>{session.title}</span>
                      <Circle size={6} className={`status-dot status-${session.status}`} fill="currentColor" />
                    </button>
                  ))}
                  <button className="session-nav new-session-nav" onClick={onNewSession}><Plus size={13} /><span>새 실행 창</span></button>
                </div>
              )}
            </div>
          )
        })}
      </nav>
      <div className="sidebar-bottom">
        <button className="remote-sidebar-button" aria-label="원격 연결" onClick={onRemote}><Network size={15} /><span>원격 연결</span><span className="remote-rail-label">Tailscale</span></button>
        <div className="sidebar-hint"><Command size={14} /><span>프로젝트와 실행을 한곳에서</span></div>
        <div className="sidebar-footer">
          <button className="settings-button" onClick={onSettings}><Settings2 size={15} /><span>설정</span></button>
          <button className="icon-button" aria-label={snapshot.theme === 'dark' ? '밝은 테마로 전환' : '어두운 테마로 전환'} title={snapshot.theme === 'dark' ? '밝은 테마' : '어두운 테마'} onClick={onTheme}>{snapshot.theme === 'dark' ? <Sun size={16} /> : <Moon size={16} />}</button>
        </div>
      </div>
    </aside>
  )
}
