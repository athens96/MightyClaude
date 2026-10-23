import type { AppSnapshot } from '../store/models';
import { selectedSession } from '../store/store';
import { PaneView } from '../pane/PaneView';
import { Sidebar } from './Sidebar';
import { useServices } from '../app/context';

export function Shell({ snapshot }: { snapshot: AppSnapshot | null }) {
  const { t } = useServices();
  if (!snapshot) return <div className="shell-loading" data-testid="loading">{t('launch.preparing')}</div>;
  const session = selectedSession(snapshot);
  return (
    <div className="shell" data-testid="shell">
      <Sidebar workspaces={snapshot.workspaces} sessions={snapshot.sessions} activeSessionId={session?.id} />
      <main className="shell-main">
        {session ? <PaneView session={session} /> : <div className="shell-empty">{t(snapshot.workspaces.length === 0 ? 'dashboard.empty' : 'layout.empty.addPane')}</div>}
      </main>
    </div>
  );
}
