// The detail column's top and bottom (spec sections 1 and 7; MainWindow.Shell.cs, AppUpdate.cs, AccountUsage.cs): the update
// countdown, the resource band, the error banner, the workspace header, the status bar and the usage popover. Their words
// are the WinUI frame's own (the host reads them from the same parts it keeps current).
import { useEffect, useRef } from 'react';
import { useServices } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { SidebarToggle } from '../dashboard/Dashboard';
import { BetaBadge, GitCapsule, MiddlePath, StatusCounts, useCall, useMenu, type MenuItem } from './parts';
import type { HeaderModel, ShellModel, UsageDetails } from './types';
import { usePageOverlay } from './overlay';
import { Icon } from '../icons/Icon';

export function Banners({ shell }: { shell: ShellModel }) {
  const { t } = useServices();
  const call = useCall();
  return (
    <>
      {shell.countdown && (
        <div className="banner banner-update" data-testid="update-countdown">
          <span className="banner-text">{shell.countdown}</span>
          <button type="button" className="plain-button" onClick={() => call('postponeUpdate')}>{t('appUpdate.countdown.later')}</button>
          <button type="button" className="accent-button" onClick={() => call('restartForUpdate')}>{t('appUpdate.countdown.restartNow')}</button>
        </div>
      )}
      {shell.resource && (
        <div className="banner banner-warning" data-testid="resource-banner">
          <span className="banner-text selectable">{shell.resource}</span>
          <button type="button" className="icon-button" aria-label={t('window.warning.dismiss')} title={t('window.warning.dismiss')} onClick={() => call('dismissResource')}>×</button>
        </div>
      )}
      {shell.error && (
        <div className="banner banner-error" role="alert" data-testid="error-banner">
          <span className="banner-text selectable">{shell.error}</span>
          <button type="button" className="icon-button" aria-label={t('window.error.dismiss')} title={t('window.error.dismiss')} onClick={() => call('dismissError')}>×</button>
        </div>
      )}
    </>
  );
}

const layoutPresets = ['grid', 'columns', 'focus', 'tabs', 'custom'] as const;

/**
 * The workspace header (M/WorkspaceView.swift workspaceHeader): the sidebar toggle, the name over the path and the Git badge,
 * the long status counts, then the layout presets (the Mac's View menu; Windows has no menu bar) and the files pane's folder.
 */
export function WorkspaceHeader({ header, counts, layoutMode }: { header: HeaderModel; counts: ShellModel['workspaces'][number]['counts'] | undefined; layoutMode: string }) {
  const { t } = useServices();
  const call = useCall();
  const menu = useMenu();
  const rename: MenuItem[] = [{ label: t('menu.renameWorkspace'), action: () => call('renameWorkspace', { id: header.id }) }];
  const copy: MenuItem[] = [{ label: t('pane.copyButton'), action: () => void navigator.clipboard?.writeText(header.path).catch(() => {}) }];
  const presets: MenuItem[] = layoutPresets.map((preset) => ({
    label: t('layout.mode.' + preset), testId: 'layout-preset-' + preset, icon: preset === layoutMode ? <Icon name="check" size={12} /> : undefined,
    action: () => call('applyLayoutPreset', { preset }),
  }));
  const layoutLabel = t('layout.mode.' + layoutMode);
  return (
    <header className="workspace-header" data-testid="workspace-header" onContextMenu={(event) => menu.atPointer(event, rename)}>
      <SidebarToggle />
      <div className="workspace-header-text">
        <div className="workspace-header-line">
          <span className="workspace-header-name">{header.name}</span>
          {header.remoteHost && <span className="capsule quiet">{header.remoteHost}</span>}
        </div>
        <div className="workspace-header-line">
          <span onContextMenu={(event) => menu.atPointer(event, copy)} className="workspace-header-path-box"><MiddlePath path={header.path} className="workspace-header-path mono" /></span>
          {header.git && <GitCapsule git={header.git} />}
        </div>
      </div>
      {counts && <StatusCounts counts={counts} long />}
      <button type="button" className="icon-button header-icon-button" title={layoutLabel} aria-label={layoutLabel} data-testid="layout-select"
        onClick={(event) => menu.under(event.currentTarget, presets)}><Icon name="layout-grid" size={15} weight={1.5} /></button>
      <button type="button" className="icon-button header-icon-button" disabled={!header.filesEnabled} title={header.filesHelp} aria-label={t('menu.showFiles')} data-testid="open-files"
        onClick={() => call('openFiles', { workspaceId: header.id })}><Icon name="folder" size={16} weight={1.5} /></button>
    </header>
  );
}

function UsagePopover({ usage, onClose }: { usage: UsageDetails; onClose: () => void }) {
  const call = useCall();
  const element = useRef<HTMLDivElement>(null);
  usePageOverlay(true);
  useEffect(() => {
    call('usageRefresh', { force: false });
    const close = (event: Event) => { if (!(event.target instanceof Node && element.current?.contains(event.target)) && !(event.target instanceof Element && event.target.closest('[data-usage-chips]'))) onClose(); };
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    window.addEventListener('mousedown', close, true); window.addEventListener('keydown', key, true);
    return () => { window.removeEventListener('mousedown', close, true); window.removeEventListener('keydown', key, true); };
  }, [call, onClose]);
  return (
    <div className="usage-popover" ref={element} role="dialog" data-page-overlay="" aria-label={usage.title} data-testid="usage-popover">
      <div className="usage-head">
        <strong>{usage.title}</strong>
        <button type="button" className="icon-button" disabled={usage.refreshing} aria-label={usage.refreshLabel} title={usage.refreshTooltip} onClick={() => call('usageRefresh', { force: true })}>
          {usage.refreshing ? <span className="spinner" /> : '↻'}
        </button>
      </div>
      {usage.cards.map((card) => (
        <div key={card.provider} className="usage-card">
          <div className="usage-card-title"><ProviderMark provider={card.provider} size={13} /><strong aria-label={card.title}>{card.name}</strong>{card.beta && <BetaBadge />}<span className="spacer" />{card.account && <span className="quiet">{card.account}</span>}</div>
          {card.windows.map((w, index) => (
            <div key={index} className="usage-window">
              <div className="usage-window-line"><span>{w.label}</span><span>{w.used}</span></div>
              <span className="usage-bar"><span className={'usage-bar-fill' + (w.warning ? ' warning' : '')} style={{ width: `${Math.round(Math.min(Math.max(w.fraction, 0), 1) * 100)}%` }} /></span>
              {w.reset && <div className="quiet small">{w.reset}</div>}
            </div>
          ))}
          {card.detail && <div className="quiet small">{card.detail}</div>}
          {card.note && <div className="quiet">{card.note}</div>}
          {card.reset && (
            <div className="usage-reset">
              <div className="strong">{card.reset.title}</div>
              {card.reset.rows.map((row) => <div key={row.program}><div className="quiet small">{row.label}</div><div>{row.text}</div></div>)}
              <button type="button" className="link-button" onClick={() => call('openUrl', { url: card.reset!.url })}>{card.reset.link}</button>
            </div>
          )}
          {card.checkedAt && <div className="tertiary small">{card.checkedAt}</div>}
        </div>
      ))}
      {usage.direct && (
        <label className="usage-direct">
          <span><span>{usage.direct.label}</span><span className="quiet small">{usage.direct.description}</span></span>
          <input type="checkbox" role="switch" checked={usage.direct.on} onChange={(event) => call('usageDirect', { on: event.target.checked })} />
        </label>
      )}
      <div className="quiet small">{usage.shared}</div>
    </div>
  );
}

export function StatusBar({ shell, usageOpen, setUsageOpen }: { shell: ShellModel; usageOpen: boolean; setUsageOpen: (open: boolean) => void }) {
  const { t } = useServices();
  const call = useCall();
  const status = shell.status;
  return (
    <footer className="status-bar" data-testid="status-bar">
      {status.remote === undefined
        ? <Icon name="monitor" size={12} weight={1.75} />
        : <span className={'status-remote-dot' + (status.remote ? ' connected' : '')} />}
      <span className="status-machine" title={status.remote === undefined ? undefined : status.machine} data-testid="status-machine">{status.machine}</span>
      <span className="spacer" />
      <span data-testid="status-panes">{status.panes}</span>
      <span className="status-dot-sep">·</span>
      <span data-testid="status-running">{status.running}</span>
      {status.update && <button type="button" className="status-update" title={status.updateHelp} onClick={() => call('openSettings')}>{status.update}</button>}
      {status.staged && (
        <span className="status-update">
          {status.staged}
          <button type="button" className="link-button" onClick={() => call('restartForUpdate')}>{t('window.status.updateRestartNow')}</button>
        </span>
      )}
      {status.setup && <button type="button" className="status-update" onClick={() => call('openSetup')}>{t('setup.finishedBadge')}</button>}
      {status.chips && (
        <>
          <span className="status-divider" />
          <button type="button" className="status-chips" data-usage-chips aria-label={status.chipsTitle} title={status.chipsTooltip} aria-expanded={usageOpen}
            onClick={() => setUsageOpen(!usageOpen)} data-testid="usage-chips">
            {status.chips.map((chip) => (
              <span key={chip.provider} className="usage-chip" aria-label={chip.name}>
                <ProviderMark provider={chip.provider} size={10} />
                {chip.words.map((word, index) => <span key={index} className={word.warning ? 'tone-text-wait' : ''}>{word.text}</span>)}
              </span>
            ))}
          </button>
        </>
      )}
      {shell.companion && (
        <span className="status-companion">
          <button type="button" className={'status-paw' + (shell.companion.petOn ? ' on' : '')} aria-pressed={shell.companion.petOn} aria-label={shell.companion.petLabel || undefined}
            title={shell.companion.petHelp ?? shell.companion.petLabel} data-testid="companion-toggle" onClick={() => call('companionToggle')}>
            <Icon name="paw-print" size={13} weight={1.75} fill={shell.companion.petOn} />
          </button>
          <button type="button" className="status-capsule" aria-label={shell.companion.statusLabel} title={shell.companion.statusLabel} data-testid="companion-status" onClick={() => call('companionStatus')}>
            <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true">{shell.companion.busy
              ? <polyline points="0,6 1.85,6 2.3,4.2 3.08,8.75 4.06,2.35 4.98,11.1 5.94,0.4 6.92,11.1 7.85,2.35 8.83,8.75 9.78,4.2 10.2,6 12,6" fill="none" stroke="currentColor" strokeWidth="0.8" strokeLinejoin="round" />
              : <g fill="none" stroke="currentColor"><circle cx="3.1" cy="3.1" r="2.2" /><circle cx="8.9" cy="3.1" r="2.2" /><circle cx="3.1" cy="8.9" r="2.2" /><circle cx="8.9" cy="8.9" r="2.2" /></g>}</svg>
            {shell.companion.count && <span>{shell.companion.count}</span>}
          </button>
        </span>
      )}
      {usageOpen && shell.usage && <UsagePopover usage={shell.usage} onClose={() => setUsageOpen(false)} />}
    </footer>
  );
}
