// The window (spec section 1): the sidebar column with its grip, and the detail column of banners, workspace header, the
// panes (layout, dashboard, welcome or the empty-panes invitation) and the status bar; the menus, the dialogs and the
// window's shortcuts (section 8) over them.
import { useCallback, useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { Dashboard, EmptyPanes, Welcome } from '../dashboard/Dashboard';
import { Dialogs } from '../dialogs/Dialogs';
import { Surfaces } from '../native/Surfaces';
import { Layout } from '../layout/Layout';
import { layoutMetrics } from '../layout/geometry';
import { Banners, StatusBar, WorkspaceHeader } from './Chrome';
import { MenuHost } from './parts';
import { shortcutAction } from './shortcuts';
import { Sidebar, sidebarSearchId } from './Sidebar';
import type { AppSnapshot } from '../store/models';

/** How far a press on the grip moves before it is a drag (DividerSlack). */
const gripSlack = 3;

function Grip({ width, setLive }: { width: number; setLive: (width: number | null) => void }) {
  const { t, bridge } = useServices();
  const press = useRef<{ start: number; width: number; moved: boolean; now: number } | null>(null);
  const down = (event: ReactPointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    press.current = { start: event.clientX, width, moved: false, now: width };
  };
  const move = (event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = press.current;
    if (!drag) return;
    const travel = event.clientX - drag.start;
    if (!drag.moved && Math.abs(travel) < gripSlack) return;
    drag.moved = true;
    const proposed = drag.width + travel;
    // Below the fold threshold the sidebar folds; the saved width stays the one from before the drag.
    if (proposed < layoutMetrics.sidebarFoldThreshold) {
      press.current = null; setLive(null);
      void bridge.call('setSidebarCollapsed', { collapsed: true }).catch(() => {});
      return;
    }
    drag.now = Math.min(Math.max(proposed, layoutMetrics.sidebarMin), layoutMetrics.sidebarMax);
    setLive(drag.now);
  };
  const up = () => {
    const drag = press.current; press.current = null;
    if (!drag?.moved) return;
    void bridge.call('setSidebarWidth', { width: drag.now }).catch(() => {}).finally(() => setLive(null));
  };
  const key = (event: ReactKeyboardEvent) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    const next = Math.min(Math.max(width + (event.key === 'ArrowRight' ? 10 : -10), layoutMetrics.sidebarMin), layoutMetrics.sidebarMax);
    void bridge.call('setSidebarWidth', { width: next }).catch(() => {});
  };
  return (
    <div className="sidebar-grip" role="separator" aria-orientation="vertical" tabIndex={0} aria-label={t('sidebar.resize')} title={t('sidebar.resizeTooltip')}
      aria-valuenow={width} aria-valuemin={layoutMetrics.sidebarMin} aria-valuemax={layoutMetrics.sidebarMax} data-testid="sidebar-grip"
      onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={() => { press.current = null; setLive(null); }} onKeyDown={key}
      onDoubleClick={() => void bridge.call('setSidebarWidth', { width: layoutMetrics.sidebarDefault }).catch(() => {})} />
  );
}

/** The window's shortcuts: the host's WinUI accelerators are cleared in web mode, so the page answers them. */
function useShortcuts(snapshot: AppSnapshot | null, blocked: boolean) {
  const { bridge } = useServices();
  const state = useRef({ snapshot, blocked });
  state.current = { snapshot, blocked };
  useEffect(() => {
    const call = (method: string, args?: unknown) => void bridge.call(method, args).catch(() => {});
    const key = (event: KeyboardEvent) => {
      const action = shortcutAction(event);
      if (!action || state.current.blocked || !state.current.snapshot) return;
      event.preventDefault();
      switch (action) {
        case 'openFolder': return call('pickFolder');
        case 'newPane': return call('newPane', { kind: 'claude' });
        case 'newShell': return call('newPane', { kind: 'shell' });
        case 'toggleSidebar': return call('setSidebarCollapsed', { collapsed: !state.current.snapshot.sidebarCollapsed });
        case 'search': {
          const focus = () => { const field = document.getElementById(sidebarSearchId) as HTMLInputElement | null; field?.focus(); field?.select(); };
          if (state.current.snapshot.sidebarCollapsed) void bridge.call('setSidebarCollapsed', { collapsed: false }).then(() => requestAnimationFrame(focus), () => {});
          else focus();
          return;
        }
        case 'settings': return call('openSettings');
        case 'files': return call('openFiles', {});
        case 'help': return call('openHelp');
        case 'textLarger': return call('textSize', { step: 1 });
        case 'textSmaller': return call('textSize', { step: -1 });
        case 'textReset': return call('textSize', { step: 0 });
      }
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [bridge]);
}

export function Shell({ snapshot }: { snapshot: AppSnapshot | null }) {
  const { t, bridge, store } = useServices();
  const state = useStoreState();
  const shell = state.shell;
  const [live, setLive] = useState<number | null>(null);
  const [usageOpen, setUsageOpen] = useState(false);
  const closeUsage = useCallback((open: boolean) => setUsageOpen(open), []);
  useEffect(() => {
    void bridge.call('shellState').then((value) => { if (value) store.dispatch({ kind: 'shell', shell: value as NonNullable<typeof shell> }); }, () => {});
  }, [bridge, store]);
  // A notification's click selected a pane: its row comes into view.
  const selected = state.selected;
  useEffect(() => {
    if (!selected) return;
    document.querySelector('.sidebar [data-session-id="' + selected.paneId + '"]')?.scrollIntoView({ block: 'nearest' });
  }, [selected]);
  useShortcuts(snapshot, state.dialogs.length > 0 || state.natives.length > 0 || (shell?.dialogOpen ?? false));
  if (!snapshot || !shell) return <div className="shell-loading" data-testid="loading">{t('launch.preparing')}</div>;
  const collapsed = snapshot.sidebarCollapsed;
  const width = live ?? snapshot.sidebarWidth;
  const header = shell.dashboard ? undefined : shell.header;
  const counts = header ? shell.workspaces.find((w) => w.id === header.id)?.counts : undefined;
  const main = shell.dashboard ? <Dashboard model={shell.dashboard} onUsage={() => setUsageOpen(true)} />
    : snapshot.workspaces.length === 0 ? <Welcome setupNeeded={shell.setupNeeded} />
    : shell.layout ? <Layout layout={shell.layout} />
    : <EmptyPanes />;
  return (
    <MenuHost>
      <div className={'shell' + (collapsed ? ' collapsed' : '')} data-testid="shell" style={{ gridTemplateColumns: collapsed ? '0 1fr' : `${width}px 1fr` }}>
        <div className="sidebar-column">
          {!collapsed && <Sidebar />}
          {!collapsed && <Grip width={width} setLive={setLive} />}
        </div>
        <div className="detail">
          <Banners shell={shell} />
          {header && <WorkspaceHeader header={header} counts={counts} layoutMode={shell.layout?.mode ?? snapshot.layout} />}
          <main className="shell-main">{main}</main>
          <StatusBar shell={shell} usageOpen={usageOpen} setUsageOpen={closeUsage} />
        </div>
      </div>
      <Surfaces />
      <Dialogs />
    </MenuHost>
  );
}
