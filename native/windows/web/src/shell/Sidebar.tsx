import { memo } from 'react';
import type { RunSession, Workspace } from '../store/models';
import { useServices } from '../app/context';

interface SidebarProps {
  workspaces: readonly Workspace[];
  sessions: readonly RunSession[];
  activeSessionId: string | undefined;
}

export function Sidebar({ workspaces, sessions, activeSessionId }: SidebarProps) {
  const { t } = useServices();
  return (
    <nav className="sidebar" aria-label={t('sidebar.workspacesHeader')}>
      <div className="sidebar-header">{t('sidebar.workspacesHeader')}</div>
      {workspaces.length === 0 && <div className="sidebar-empty">{t('dashboard.empty')}</div>}
      {workspaces.map((workspace) => (
        <section key={workspace.id} className="sidebar-workspace" data-workspace-id={workspace.id}>
          <div className="sidebar-workspace-name" title={workspace.path}>{workspace.name}</div>
          {sessions.filter((session) => session.workspaceId === workspace.id).map((session) => (
            <PaneRow key={session.id} id={session.id} title={session.title} status={session.status} active={session.id === activeSessionId} />
          ))}
        </section>
      ))}
    </nav>
  );
}

/** One pane's row; drawn again only when its title, status or selection changes, not on every streamed line. */
const PaneRow = memo(function PaneRow({ id, title, status, active }: { id: string; title: string; status: string; active: boolean }) {
  const { bridge, t } = useServices();
  return (
    <button
      type="button"
      className={'sidebar-pane' + (active ? ' active' : '')}
      data-session-id={id}
      aria-current={active ? 'true' : undefined}
      onClick={() => void bridge.call('selectSession', { id }).catch(() => {})}
    >
      <span className={'status-dot status-' + status} aria-hidden="true" />
      <span className="sidebar-pane-title">{title}</span>
      <span className="sidebar-pane-status">{t('session.state.' + status)}</span>
    </button>
  );
});
