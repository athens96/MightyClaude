// The work-status page (spec section 5; MainWindow.Dashboard.cs, DashboardWorkspaces.cs) and the empty states (section 6;
// MainWindow.EmptyStates.cs). The counts, the order of the rows and their words are the host's (WorkDashboard).
import { useServices, useStoreState } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { useAddMenu } from '../shell/addMenu';
import { BetaBadge, GitCapsule, MiddlePath, StatusGlyph, useCall, usePaneInfo } from '../shell/parts';
import type { DashboardModel } from '../shell/types';
import { Icon } from '../icons/Icon';

export function SidebarToggle() {
  const { t } = useServices();
  const call = useCall();
  const collapsed = useStoreState().snapshot?.sidebarCollapsed ?? false;
  const label = t(collapsed ? 'sidebar.expand' : 'sidebar.collapse') + ' (Ctrl+B)';
  return (
    <button type="button" className="icon-button sidebar-toggle" title={label} aria-label={label} data-testid="sidebar-toggle"
      onClick={() => call('setSidebarCollapsed', { collapsed: !collapsed })}>
      <Icon name="panel-left" size={16} weight={1.5} />
    </button>
  );
}

function Bar({ fraction, warning }: { fraction: number; warning: boolean }) {
  return <span className="usage-bar"><span className={'usage-bar-fill' + (warning ? ' warning' : '')} style={{ width: `${Math.round(Math.min(Math.max(fraction, 0), 1) * 100)}%` }} /></span>;
}

export function Dashboard({ model, onUsage }: { model: DashboardModel; onUsage: () => void }) {
  const { t } = useServices();
  const call = useCall();
  const panes = usePaneInfo();
  const addMenu = useAddMenu();
  const tiles: [string, number, string][] = [['running', model.running, 'phone.dashboard.stat.running'], ['waiting', model.waiting, 'phone.dashboard.stat.waiting'], ['done', model.done, 'phone.dashboard.stat.done']];
  return (
    <div className="dashboard" data-testid="dashboard">
      <div className="dashboard-heading">
        <SidebarToggle />
        <div>
          <h1 className="dashboard-title">{t('phone.dashboard.title')}</h1>
          <div className="dashboard-subtitle">{model.subtitle}</div>
        </div>
      </div>
      <div className={'dashboard-tiles' + (model.usage ? ' with-usage' : '')}>
        {tiles.map(([key, count, label]) => (
          <div key={key} className={'dashboard-tile ' + key} data-testid={'dashboard-stat-' + key}>
            <span className="dashboard-tile-count">{count}</span>
            <span className="dashboard-tile-label">{t(label)}</span>
          </div>
        ))}
        {model.usage && (
          <button type="button" className="dashboard-usage" onClick={onUsage} data-testid="dashboard-usage">
            <span className="dashboard-usage-top"><strong>{t('dashboard.usage.title')}</strong><span>{t('dashboard.usage.shared')}</span></span>
            {model.usage.map((card) => (
              <span key={card.provider} className="dashboard-usage-row">
                <span className="dashboard-usage-name"><ProviderMark provider={card.provider} size={12} />{card.name}</span>
                {card.bars.map((bar) => (
                  <span key={bar.label} className="dashboard-usage-window">{bar.label}<Bar fraction={bar.fraction} warning={bar.warning} /><strong>{bar.percent}</strong></span>
                ))}
              </span>
            ))}
          </button>
        )}
      </div>
      {model.workspaces.length === 0 && <div className="dashboard-empty">{t('dashboard.empty')}</div>}
      {model.workspaces.map((workspace) => (
        <section key={workspace.id} className="dashboard-workspace" data-testid="dashboard-workspace">
          <div className="dashboard-workspace-head">
            <button type="button" className="dashboard-workspace-name" onClick={() => call('selectWorkspace', { id: workspace.id })}>{workspace.name}</button>
            <MiddlePath path={workspace.path} className="mono" />
            {workspace.git && <GitCapsule git={workspace.git} />}
            <span className="spacer" />
            {workspace.local && <button type="button" className="dashboard-button" title={t('menu.showFiles')} onClick={() => call('openFiles', { workspaceId: workspace.id })}><Icon name="folder" size={13} weight={1.75} />{t('files.pane.title')}</button>}
            <button type="button" className="dashboard-button strong" title={t('workspace.addPaneHelp')} aria-label={t('workspace.addPaneAccessibility', { workspace: workspace.name })}
              onClick={(event) => addMenu(event.currentTarget, { workspaceId: workspace.id })}><Icon name="plus" size={13} weight={2.4} />{t('workspace.addPane')}</button>
          </div>
          {workspace.rows.length === 0 ? <div className="dashboard-none">{t('phone.workspaces.noSessions')}</div> : (
            <div className="dashboard-rows">
              {workspace.rows.map((id) => {
                const info = panes.get(id);
                if (!info) return null;
                return (
                  <button key={id} type="button" className="dashboard-row" data-session-id={id} onClick={() => { call('hideDashboard'); call('selectSession', { id }); }}>
                    <StatusGlyph tone={info.tone} size={18} kind={info.kind} />
                    <span className="dashboard-row-text">
                      <span className={'dashboard-row-title' + (info.settled ? '' : ' strong')}>{info.title}</span>
                      <span className="dashboard-row-meta">
                        {info.kind === 'claude' && <><ProviderMark provider={info.provider} size={12} /><span>{info.agentLabel ?? info.provider}</span>{info.beta && <BetaBadge />}</>}
                        <span>{info.dashMeta}</span>
                        {info.kind === 'shell' && <span className="tone-text-wait strong">{t('phone.card.localTerminal')}</span>}
                      </span>
                      {info.activity && <span className={'dashboard-row-activity mono' + (info.activityError ? ' tone-text-err' : '')}>{info.activity}</span>}
                    </span>
                    {info.dashWait && <span className="dashboard-row-wait tone-text-wait">{info.dashWait}</span>}
                  </button>
                );
              })}
            </div>
          )}
        </section>
      ))}
    </div>
  );
}

/** No project yet (BuildWelcome; M/WorkspaceView.swift welcome): the split mark, the title, the two lines, the open and connect buttons, the shortcuts, and the platform line at the foot. */
export function Welcome({ setupNeeded }: { setupNeeded: boolean }) {
  const { t } = useServices();
  const call = useCall();
  return (
    <div className="welcome" data-testid="welcome">
      <div className="welcome-toggle"><SidebarToggle /></div>
      <span className="welcome-spacer" />
      <Icon name="columns-2" size={58} weight={0.9} className="welcome-mark" />
      <h1 className="welcome-title">{t('layout.welcome.title')}</h1>
      <p className="welcome-body">{t('layout.welcome.body')}</p>
      <div className="welcome-buttons">
        <button type="button" className="accent-button with-icon" onClick={() => call('pickFolder')}><Icon name="folder-plus" size={15} />{t('layout.welcome.openProject')}</button>
        <button type="button" className="plain-button with-icon" onClick={() => call('connectRemote')}><Icon name="globe" size={14} />{t('remote.menu.connect')}</button>
        {setupNeeded && <button type="button" className="plain-button" onClick={() => call('openSetup')}>{t('setup.title')}</button>}
      </div>
      <div className="welcome-note welcome-shortcuts">{t('layout.welcome.shortcuts')}</div>
      <span className="welcome-spacer" />
      <div className="welcome-note welcome-footer">{t('layout.welcome.footer')}</div>
    </div>
  );
}

/** A project without panes (BuildEmptyPanes). */
export function EmptyPanes() {
  const { t } = useServices();
  const call = useCall();
  return (
    <div className="empty-panes" data-testid="empty-panes">
      <h2>{t('layout.empty.addPane')}</h2>
      <p>{t('layout.empty.addPaneDetail')}</p>
      <button type="button" className="accent-button" onClick={() => call('newPane', { kind: 'claude' })}>{t('workspace.newAgentPane', { provider: 'Claude' })}</button>
    </div>
  );
}
