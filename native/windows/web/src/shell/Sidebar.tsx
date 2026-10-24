// The sidebar (spec section 2; MainWindow.Sidebar.cs, Navigation.cs, Delegation.cs): search, the work-status entry, the
// workspaces with their panes and delegated children, the layout choice and the footer. Every row's words, counts and state
// are the host's (WebShellModel); every click runs a window action.
import { memo, useState } from 'react';
import { useServices, useStoreState } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { activeSessionId } from '../store/store';
import { useAddMenu } from './addMenu';
import { filterWorkspaces } from './filter';
import { BetaBadge, StatusCounts, StatusGlyph, useCall, useMenu, usePaneInfo, useShell, type MenuItem } from './parts';
import type { CardAction, ChildCard, PaneInfo, SidebarItem, SidebarWorkspace } from './types';

export const sidebarSearchId = 'sidebar-search';

/** Makes a pane the active one: drawn at once, then the host's SelectLayoutSession saves it. */
export function useChoosePane(): (id: string, method?: 'selectSession' | 'activatePane') => void {
  const { bridge, store } = useServices();
  return (id: string, method: 'selectSession' | 'activatePane' = 'selectSession') => {
    store.dispatch({ kind: 'choose', paneId: id });
    // A choice the host turned down (a dialog was open) is forgotten after a while; one it took is forgotten as its state arrives.
    void bridge.call(method, { id }).catch(() => {}).finally(() => setTimeout(() => { if (store.getState().chosen === id) store.dispatch({ kind: 'choose', paneId: undefined }); }, 1500));
  };
}

function childMenu(call: (method: string, args?: unknown) => void, id: string, actions: readonly CardAction[] | undefined): MenuItem[] {
  return (actions ?? []).map((a) => ({ label: a.label, action: () => call('childAction', { id, action: a.action }) }));
}

function ChildCardLine({ id, card }: { id: string; card: ChildCard }) {
  const { t } = useServices();
  const call = useCall();
  return (
    <div className="child-card" data-testid={'child-card-' + id}>
      {card.busy ? <div className="child-card-busy"><span className="spinner" />{t('delegation.card.working')}</div> : card.actions.length > 0 && (
        <div className="child-card-actions">
          {card.actions.map((a) => (
            <button key={a.action} type="button" className={'child-card-action' + (a.action === 'discard' ? ' quiet' : '')} title={a.help} aria-label={a.help}
              onClick={() => call('childAction', { id, action: a.action })}>{a.label}</button>
          ))}
        </div>
      )}
      {card.held && <div className="child-card-held" title={t('delegation.card.heldNoticesHelp')}>{card.held}</div>}
      {card.note && <div className={'child-card-note tone-text-' + (card.noteTone ?? 'idle')}>{card.note}</div>}
      {card.kept && <div className="child-card-note tone-text-wait">{card.kept}</div>}
    </div>
  );
}

/** A pane's row (SessionIndicator): glyph, title in its weight, beta, meta line, waiting words; a delegated child's state word instead. */
const PaneRow = memo(function PaneRow({ info, active, child }: { info: PaneInfo; active: boolean; child?: Extract<SidebarItem, { type: 'pane' }> }) {
  const { t } = useServices();
  const call = useCall();
  const menu = useMenu();
  const choose = useChoosePane();
  const items: MenuItem[] = [
    { label: t('menu.rename'), action: () => call('renameSession', { id: info.id }) },
    { label: t('menu.closePane'), action: () => call('closeSession', { id: info.id }) },
  ];
  if (child?.menu?.length) items.push({ separator: true }, ...childMenu(call, info.id, child.menu));
  return (
    <button type="button" className={'sidebar-pane' + (active ? ' active' : '')} data-session-id={info.id} data-testid="sidebar-pane" aria-current={active ? 'true' : undefined}
      onClick={() => choose(info.id)} onContextMenu={(event) => menu.atPointer(event, items)}>
      <StatusGlyph tone={info.tone} />
      <span className="sidebar-pane-text">
        <span className="sidebar-pane-line">
          <span className={'sidebar-pane-title' + (info.settled ? '' : ' strong')} title={info.help}>{info.title}</span>
          {info.beta && <BetaBadge />}
        </span>
        <span className="sidebar-pane-meta">
          {info.agent && <ProviderMark provider={info.agent} size={11} />}
          {info.agent && <span>{info.agentLabel}</span>}
          <span className="sidebar-pane-detail">{info.meta}</span>
        </span>
      </span>
      {child?.stateWord ? <span className={'sidebar-pane-wait tone-text-' + (child.stateTone ?? 'idle')}>{child.stateWord}</span>
        : info.wait && <span className="sidebar-pane-wait tone-text-wait">{info.wait}</span>}
    </button>
  );
});

function WorkspaceGroup({ workspace, selected, activeId, panes, dashboard }: { workspace: SidebarWorkspace; selected: boolean; activeId?: string; panes: Map<string, PaneInfo>; dashboard: boolean }) {
  const { t } = useServices();
  const call = useCall();
  const menu = useMenu();
  const addMenu = useAddMenu();
  const items: MenuItem[] = [
    { label: t('menu.rename'), action: () => call('renameWorkspace', { id: workspace.id }) },
    { label: t('workspace.menu.remove'), action: () => call('removeWorkspace', { id: workspace.id }) },
  ];
  if (workspace.local) items.push(
    { label: t('menu.showInExplorer'), action: () => call('showInExplorer', { id: workspace.id }) },
    { separator: true },
    { label: t('menu.showFiles'), action: () => call('openFiles', { workspaceId: workspace.id }) },
  );
  const disclosure = t(workspace.expanded ? 'workspace.collapseAccessibility' : 'workspace.expandAccessibility', { workspace: workspace.name });
  return (
    <section className={'sidebar-workspace' + (selected ? ' selected' : '')} data-workspace-id={workspace.id} data-testid="sidebar-workspace">
      <div className="sidebar-workspace-row" onContextMenu={(event) => menu.atPointer(event, items)}>
        <button type="button" className="sidebar-workspace-select" title={workspace.path} onClick={() => call('selectWorkspace', { id: workspace.id })}>
          <svg className="sidebar-workspace-icon" width="14" height="14" viewBox="0 0 16 16" aria-hidden="true">
            <path d={workspace.local ? 'M1.5 4h4.5l1.2 1.6h7.3V13h-13z' : 'M2 3h12v8H2zM5 14h6M8 11v3'} fill="none" stroke="currentColor" strokeWidth="1.2" />
          </svg>
          <span className="sidebar-workspace-names">
            <span className="sidebar-workspace-name">{workspace.name}</span>
            {workspace.remoteHost && <span className="sidebar-workspace-host">{workspace.remoteHost}</span>}
          </span>
          <StatusCounts counts={workspace.counts} />
        </button>
        <button type="button" className="sidebar-disclosure" aria-label={disclosure} title={disclosure} aria-expanded={workspace.expanded}
          onClick={() => call('toggleDisclosure', { id: workspace.id })}>
          <svg width="8" height="8" viewBox="0 0 8 8" aria-hidden="true"><path d={workspace.expanded ? 'M1 2.5l3 3 3-3' : 'M2.5 1l3 3-3 3'} fill="none" stroke="currentColor" strokeWidth="1.3" /></svg>
        </button>
      </div>
      {workspace.expanded && (
        <div className="sidebar-workspace-panes">
          {(workspace.items ?? []).map((item) => {
            if (item.type === 'parentClosed') return (
              <div key={'closed-' + item.id} className="sidebar-parent-closed" title={item.help} aria-label={item.name}>
                <span>{item.label}</span>{item.branch && <span className="mono">{item.branch}</span>}<span className="mono">{item.count}</span>
              </div>
            );
            if (item.type === 'closedChild') return (
              <div key={item.id} className="sidebar-child" style={{ paddingLeft: item.depth * 20 }}>
                <div className="sidebar-closed-child" data-testid={'sidebar-child-' + item.id} onContextMenu={(event) => item.menu.length > 0 && menu.atPointer(event, childMenu(call, item.id, item.menu))}>
                  <StatusGlyph tone={item.tone} />
                  <span className="sidebar-pane-title" title={item.title}>{item.title}</span>
                  <span className={'sidebar-pane-wait tone-text-' + item.stateTone}>{item.stateWord}</span>
                </div>
                {item.card && <ChildCardLine id={item.id} card={item.card} />}
              </div>
            );
            const info = panes.get(item.id);
            if (!info) return null;
            return (
              <div key={item.id} className="sidebar-child" style={{ paddingLeft: item.depth * 20 }}>
                <PaneRow info={info} active={!dashboard && item.id === activeId} child={item.depth > 0 ? item : undefined} />
                {item.card && <ChildCardLine id={item.id} card={item.card} />}
              </div>
            );
          })}
          <button type="button" className="sidebar-add" title={t('workspace.addPaneHelp')} aria-label={t('workspace.addPaneAccessibility', { workspace: workspace.name })}
            data-testid="sidebar-add" onClick={(event) => addMenu(event.currentTarget, { workspaceId: workspace.id })}>
            <span aria-hidden="true">+</span>{t('workspace.addPane')}
          </button>
        </div>
      )}
    </section>
  );
}

const presets = ['grid', 'columns', 'focus', 'tabs', 'custom'] as const;

export function Sidebar() {
  const { t } = useServices();
  const state = useStoreState();
  const shell = useShell();
  const panes = usePaneInfo();
  const call = useCall();
  const [search, setSearch] = useState('');
  const snapshot = state.snapshot;
  if (!snapshot || !shell) return <nav className="sidebar" />;
  const dashboard = shell.dashboard !== undefined;
  const listed = filterWorkspaces(shell.workspaces, search);
  const activeId = activeSessionId(state);
  const entryCounts = shell.entry;
  return (
    <nav className="sidebar" aria-label={t('sidebar.workspacesHeader')} data-testid="sidebar">
      <div className="sidebar-search">
        <input id={sidebarSearchId} type="search" placeholder={t('sidebar.searchPlaceholder')} aria-label={t('sidebar.searchPlaceholder')} value={search}
          onChange={(event) => setSearch(event.target.value)} />
      </div>
      <button type="button" className={'sidebar-entry' + (dashboard ? ' selected' : '')} title={t('dashboard.sidebarHelp')} data-testid="sidebar-dashboard"
        onClick={() => call(dashboard ? 'hideDashboard' : 'showDashboard')}>
        <span className="sidebar-entry-tile" aria-hidden="true"><StatusGlyph tone="run" size={12} /></span>
        <span className="sidebar-entry-title">{t('phone.dashboard.title')}</span>
        <StatusCounts counts={entryCounts} />
      </button>
      <div className="sidebar-section">
        <span className="sidebar-section-title">{t('sidebar.workspacesHeader')}</span>
        <span className="sidebar-section-count">{shell.workspaces.length}</span>
        <span className="spacer" />
        <button type="button" className="icon-button" title={t('menu.openProject')} aria-label={t('menu.openProject')} onClick={() => call('pickFolder')}>
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><path d="M1.5 4h4.5l1.2 1.6h7.3V13h-13zM8 7.5v4M6 9.5h4" fill="none" stroke="currentColor" strokeWidth="1.2" /></svg>
        </button>
        <button type="button" className="icon-button" title={t('remote.menu.connect')} aria-label={t('remote.menu.connect')} onClick={() => call('connectRemote')}>
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><path d="M2 3h12v7H2zM5 13h6M8 10v3" fill="none" stroke="currentColor" strokeWidth="1.2" /></svg>
        </button>
      </div>
      <div className="sidebar-list">
        {listed.length === 0 && (
          <div className="sidebar-empty">
            <div>{t(search.trim().length > 0 ? 'sidebar.noSearchResults' : 'dashboard.empty')}</div>
            <button type="button" className="sidebar-open-folder" onClick={() => call('pickFolder')}>{t('sidebar.openFolder')}</button>
          </div>
        )}
        {listed.map((workspace) => (
          <WorkspaceGroup key={workspace.id} workspace={workspace} panes={panes} activeId={activeId} dashboard={dashboard}
            selected={!dashboard && snapshot.activeWorkspaceId === workspace.id} />
        ))}
      </div>
      <div className="sidebar-layout">
        <select aria-label={t('layout.mode.custom')} value={shell.layout?.mode ?? snapshot.layout} data-testid="layout-select"
          onChange={(event) => call('applyLayoutPreset', { preset: event.target.value })}>
          {presets.map((preset) => <option key={preset} value={preset}>{t('layout.mode.' + preset)}</option>)}
        </select>
      </div>
      <footer className="sidebar-footer">
        <span className="sidebar-brand">
          <span className="sidebar-brand-name">Mighty Claude</span> <BetaBadge />
          <span className="sidebar-version mono">v{shell.version}</span>
        </span>
        <button type="button" className="icon-button" title={t('menu.help')} aria-label={t('menu.help')} onClick={() => call('openHelp')}>?</button>
        <button type="button" className="icon-button" title={t('sidebar.toggleTheme')} aria-label={t('sidebar.toggleTheme')} data-testid="theme-toggle"
          onClick={() => call('setTheme', { theme: snapshot.theme === 'light' ? 'dark' : 'light' })}>{snapshot.theme === 'light' ? '☾' : '☀'}</button>
        <button type="button" className="icon-button" title={t('settings.settingsWindowTitle')} aria-label={t('settings.settingsWindowTitle')} onClick={() => call('openSettings')}>
          <svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="2.2" fill="none" stroke="currentColor" strokeWidth="1.2" /><path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" stroke="currentColor" strokeWidth="1.2" /></svg>
        </button>
      </footer>
    </nav>
  );
}
