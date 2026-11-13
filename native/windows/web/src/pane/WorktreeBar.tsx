// The bar over an agent pane in its own worktree (pane/worktree.ts): branch → base, how far it is (↑ ↓ ±), its PORT, its setup,
// and Merge / Commit and merge, Ask to resolve and the rest in its menu. The host does the git (MainWindow.PaneWorktree.cs).
import { useCallback, useEffect, useState } from 'react';
import { useServices } from '../app/context';
import { Icon } from '../icons/Icon';
import { useMenu } from '../shell/parts';
import type { RunSession } from '../store/models';
import { worktreeBar, type WorktreeStatus } from './worktree';
import './worktree.css';

export function WorktreeBar({ session }: { session: RunSession }) {
  const { t, bridge } = useServices();
  const menu = useMenu();
  const pane = session.paneWorktree!;
  const [status, setStatus] = useState<WorktreeStatus | null>(null);
  const [working, setWorking] = useState(false);
  const refresh = useCallback(() => {
    void bridge.call<WorktreeStatus | null>('paneWorktreeStatus', { id: session.id }).then((value) => setStatus(value ?? null), () => {});
  }, [bridge, session.id]);
  // Read again when a run ends and every 20 seconds (the Mac's bar).
  useEffect(() => { refresh(); const timer = setInterval(refresh, 20000); return () => clearInterval(timer); }, [refresh, session.status]);
  const act = (action: string) => {
    setWorking(true);
    void bridge.call('paneWorktreeAction', { id: session.id, action }).finally(() => { setWorking(false); refresh(); });
  };
  const model = worktreeBar(pane, status, t);
  const busy = working || !!status?.busy;
  return (
    <div className="worktree-bar" data-testid={'pane-worktree-bar-' + session.id}>
      <Icon name="git-branch" size={12} weight={2.2} />
      <span className="worktree-bar-branch" title={model.help}>{model.branch}</span>
      {model.ahead && <span className="worktree-bar-count" title={model.ahead.help}>{model.ahead.text}</span>}
      {model.behind && <span className="worktree-bar-count tone-text-wait" title={model.behind.help}>{model.behind.text}</span>}
      {model.changed && <button type="button" className="worktree-bar-count link" title={model.changed.help} onClick={() => act('changes')}>{model.changed.text}</button>}
      <button type="button" className="worktree-bar-port link" title={model.port.help} onClick={() => act('port')}>{model.port.text}</button>
      {model.setup && <span className={'worktree-bar-setup tone-text-' + model.setup.tone}>{model.setup.text}</span>}
      <span className="spacer" />
      {model.resolve && <button type="button" className="btn small" title={model.resolve.help} disabled={busy} onClick={() => act('resolve')} data-testid="pane-worktree-resolve">{model.resolve.label}</button>}
      <button type="button" className="btn small primary" title={model.primary.help} disabled={busy} onClick={() => act(model.primary.action)} data-testid="pane-worktree-merge">{model.primary.label}</button>
      <button type="button" className="icon-button" title={t('paneWorktree.bar.more')} aria-label={t('paneWorktree.bar.more')} disabled={busy}
        onClick={(event) => menu.under(event.currentTarget, model.menu.map((item) => ({ label: item.label, action: () => act(item.action) })))}><Icon name="ellipsis" size={14} weight={2.4} /></button>
    </div>
  );
}
