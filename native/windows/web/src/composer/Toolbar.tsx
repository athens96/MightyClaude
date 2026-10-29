import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { useServices } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { clearUnsupported, contextLabel, unsupportedSettings, type MenuName, type MenuRow, type Pills, type RunSettingsInfo, type RunSettingsValues, type SessionInfo } from './menus';
import { usePageOverlay } from '../shell/overlay';
import { BetaBadge } from '../shell/parts';
import { Icon, type IconName } from '../icons/Icon';
import type { Translate } from '../i18n/locale';
import type { AskTargetOption } from './types';
import { runSettingsEnter } from './keys';

/** A toolbar narrower than this folds effort, permission and Fast into one options menu (ArrangeComposer's overflow). */
const narrowToolbar = 420;

type Open = { kind: 'menu'; name: MenuName; rows: MenuRow[] } | { kind: 'runSettings'; info: RunSettingsInfo } | { kind: 'sessionInfo'; info: SessionInfo } | null;

/** Closes a popover on a press outside it or on Esc. */
export function usePopover(close: () => void) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const press = (event: PointerEvent) => { if (ref.current && !ref.current.contains(event.target as Node)) close(); };
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') close(); };
    document.addEventListener('pointerdown', press, true);
    document.addEventListener('keydown', key);
    return () => { document.removeEventListener('pointerdown', press, true); document.removeEventListener('keydown', key); };
  }, [close]);
  return ref;
}

/** A popover menu's rows (the WinUI MenuFlyout's, read row by row): headers, separators, choices, actions and submenus. */
export function MenuList({ rows, path, choose }: { rows: MenuRow[]; path: number[]; choose: (path: number[], row: MenuRow) => void }) {
  const [opened, setOpened] = useState<number | null>(null);
  return (
    <>
      {rows.map((row, index) => {
        const at = [...path, index];
        switch (row.kind) {
          case 'separator': return <div key={index} className="menu-separator" role="separator" />;
          case 'header': return <div key={index} className="menu-header">{row.text}</div>;
          case 'submenu':
            return (
              <div key={index} className="menu-submenu">
                <button type="button" role="menuitem" className="menu-row" disabled={!row.enabled} aria-expanded={opened === index} onClick={() => setOpened(opened === index ? null : index)}>
                  <span className="menu-check" />{row.text}<span className="menu-chevron">›</span>
                </button>
                {opened === index && <div className="menu-nested"><MenuList rows={row.items} path={at} choose={choose} /></div>}
              </div>
            );
          default: {
            const enabled = row.kind === 'runSettings' ? true : row.enabled;
            const checked = row.kind === 'choice' && row.checked;
            const help = row.kind === 'choice' || row.kind === 'action' ? row.help ?? undefined : undefined;
            return (
              <button key={index} type="button" role={row.kind === 'choice' ? 'menuitemradio' : 'menuitem'} aria-checked={row.kind === 'choice' ? checked : undefined} className="menu-row" disabled={!enabled} title={help} onClick={() => choose(at, row)}>
                <span className="menu-check">{checked ? '✓' : ''}</span>{row.text}
              </button>
            );
          }
        }
      })}
    </>
  );
}

export function RunSettingsPopover({ paneId, info, busy = info.held, close }: { paneId: string; info: RunSettingsInfo; busy?: boolean; close: () => void }) {
  const { bridge, t } = useServices();
  const [values, setValues] = useState<RunSettingsValues>(info.values);
  const [problem, setProblem] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const applying = useRef(false);
  // The live composer state wins over the opening snapshot, including when a mobile send starts or the queue finishes.
  const held = busy || saving;
  const ref = usePopover(close);
  const set = (change: Partial<RunSettingsValues>) => setValues((current) => ({ ...current, ...change }));
  const apply = async () => {
    if (busy || applying.current) return;
    applying.current = true; setSaving(true); setProblem(null);
    try {
      const refusal = await bridge.call<string | null>('applyRunSettings', { paneId, ...values });
      if (refusal !== null && refusal !== undefined) setProblem(refusal);
      else close();
    } catch (failure) {
      setProblem(failure instanceof Error ? failure.message : String(failure));
    } finally {
      applying.current = false; setSaving(false);
    }
  };
  return (
    <div ref={ref} className="composer-popover run-settings" data-testid="run-settings" data-page-overlay="" role="dialog" aria-label={t('settings.run.title')} onKeyDown={(event) => {
      if (event.key !== 'Enter' || event.target instanceof HTMLButtonElement || event.target instanceof HTMLSelectElement) return;
      const action = runSettingsEnter({ ...event, keyCode: event.nativeEvent.keyCode, isComposing: event.nativeEvent.isComposing });
      if (action === 'pass') return;
      event.preventDefault();
      if (action === 'apply' && !held) void apply();
    }}>
      <div className="popover-header"><strong>{t('settings.run.title')}</strong><span>{info.provider}</span>{info.beta && <span className="beta-badge">Beta</span>}</div>
      <fieldset className="popover-body run-settings-fields" disabled={held}>
        <section>
          <div className="popover-strong">{info.model}</div>
          {info.modelDescription && <div className="popover-quiet">{info.modelDescription}</div>}
          <div className="popover-quiet">{info.modelSource}</div>
        </section>
        <section>
          <div className="popover-strong">{info.permission}</div>
          <div className="popover-quiet">{info.permissionDescription}</div>
          {info.approval && <div className="popover-quiet">{info.approval}</div>}
          {info.fastSupported && <div className="popover-quiet">{t('settings.run.fastModeNote')}</div>}
        </section>
        {info.webSearch && (
          <section>
            <label className="popover-field">
              <span>{t('settings.run.webSearchLabel')}</span>
              <select value={values.webSearch} onChange={(event) => set({ webSearch: event.target.value })}>
                {[['default', 'settings.run.webSearchDefault'], ['disabled', 'settings.run.webSearchOff'], ['cached', 'settings.run.webSearchCached'], ['live', 'settings.run.webSearchLive']].map(([value, key]) => <option key={value} value={value}>{t(key!)}</option>)}
              </select>
            </label>
            <div className="popover-quiet">{info.searchDescriptions[values.webSearch] ?? ''}</div>
          </section>
        )}
        {info.networkAccess && (
          <section>
            <label className="popover-field">
              <span>{t('settings.run.shellNetworkToggle')}</span>
              <input type="checkbox" role="switch" checked={values.networkAccess} disabled={!info.networkAllowed} onChange={(event) => set({ networkAccess: event.target.checked })} />
            </label>
            <div className="popover-quiet">{info.networkNote}</div>
          </section>
        )}
        {(info.maxTurns || info.maxBudgetUsd) && (
          <section>
            <div className="popover-strong">{t('settings.run.limitsTitle')}</div>
            {info.maxTurns && <label className="popover-stack"><span>{t('settings.run.maxTurnsLabel')}</span><input inputMode="numeric" value={values.maxTurns} placeholder={t('settings.run.unlimitedPlaceholder')} onChange={(event) => set({ maxTurns: event.target.value })} /></label>}
            {info.maxBudgetUsd && <label className="popover-stack"><span>{t('settings.run.maxBudgetLabel')}</span><input inputMode="decimal" value={values.maxBudgetUsd} placeholder={t('settings.run.unlimitedPlaceholder')} onChange={(event) => set({ maxBudgetUsd: event.target.value })} /></label>}
            <div className="popover-quiet">{t('settings.run.limitsNote')}</div>
          </section>
        )}
        {unsupportedSettings(info, values) && (
          <section>
            <div className="popover-quiet">{t('settings.run.unsupportedNotice')}</div>
            <button type="button" className="composer-text-button" onClick={() => setValues(clearUnsupported(info, values))}>{t('settings.run.unsupportedResetButton')}</button>
          </section>
        )}
      </fieldset>
      {busy && <div className="popover-quiet run-settings-held" role="status">{t('phone.session.settingsLocked')}</div>}
      {problem && <div className="popover-problem" role="alert">{problem}</div>}
      <div className="popover-footer">
        <span className="popover-quiet">{t('settings.run.appliesNextRequest')}</span>
        <button type="button" className="popover-button" onClick={close}>{t('settings.run.cancelButton')}</button>
        <button type="button" className="popover-button prominent" data-testid="run-settings-apply" disabled={held} onClick={() => void apply()}>{t('settings.run.applyButton')}</button>
      </div>
    </div>
  );
}

/** The context ring (M/SessionInfoViews.swift SessionContextRing): a 2pt track, the used share in the accent (amber from 95%), the percent. */
export function ContextRing({ percent, size }: { percent: number | null; size: number }) {
  const r = (size - 2) / 2;
  const length = 2 * Math.PI * r;
  const share = percent === null ? 0 : Math.min(1, Math.max(0, percent / 100));
  return (
    <svg className={'context-ring-mark' + (percent !== null && percent >= 95 ? ' high' : '')} width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="currentColor" strokeOpacity={0.12} strokeWidth={2} />
      {percent !== null && <circle className="context-ring-arc" cx={size / 2} cy={size / 2} r={r} fill="none" strokeWidth={2} strokeLinecap="round"
        strokeDasharray={`${share * length} ${length}`} transform={`rotate(-90 ${size / 2} ${size / 2})`} />}
      <text x="50%" y="50%" dominantBaseline="central" textAnchor="middle" className="context-ring-text" fontSize={size > 32 ? 13 : 8.5}>{contextLabel(percent)}</text>
    </svg>
  );
}

function SessionInfoPopover({ info, close }: { info: SessionInfo; close: () => void }) {
  const { t } = useServices();
  const [shown, setShown] = useState(false);
  const ref = usePopover(close);
  return (
    <div ref={ref} className="composer-popover session-info" data-testid="session-info" data-page-overlay="" role="dialog" aria-label={t('composer.sessionInfo.title')}>
      {/* M/SessionInfoViews.swift SessionInfoView: the mark, the title over the agent, the state at the right. */}
      <div className="session-info-header">
        <ProviderMark provider={info.provider} size={20} />
        <div className="session-info-heading"><strong title={info.title}>{info.title}</strong><span className="popover-quiet">{info.providerLabel}{info.beta && <> <BetaBadge /></>}</span></div>
        <span className="session-info-state"><span className={'status-dot tone-' + info.tone.toLowerCase()} />{info.status}</span>
      </div>
      <div className="popover-body">
        <section className="session-info-context">
          <span className="context-ring large"><ContextRing percent={info.contextPercent} size={44} /></span>
          <div><div className="session-info-context-title">{t('composer.sessionInfo.contextUsage')}</div><div className="popover-quiet">{info.context}</div></div>
        </section>
        <dl className="session-info-rows">
          {info.rows.map((row) => <div key={row.key} className={'session-info-row row-' + row.key}>{row.label && <dt>{row.label}</dt>}<dd>{row.value}</dd></div>)}
        </dl>
        <div className="session-info-divider" role="separator" />
        {info.tokenScope && <div className="session-info-scope">{info.tokenScope}</div>}
        {info.tokens.length > 0 && <dl className="session-info-rows">{info.tokens.map((row) => <div key={row.key} className="session-info-row token"><dt>{row.label}</dt><dd>{row.value}</dd></div>)}</dl>}
        {info.note && <div className="session-info-foot">{info.note}</div>}
        {info.cost && <dl className="session-info-rows"><div className="session-info-row"><dt>{info.cost.label}</dt><dd>{info.cost.value}</dd></div></dl>}
        {info.noUsage && <div className="popover-quiet">{t('composer.sessionInfo.noUsage')}</div>}
        {info.identifiers.length > 0 && (
          <div>
            <button type="button" className="session-info-disclosure" aria-expanded={shown} onClick={() => setShown(!shown)}><Icon name={shown ? 'chevron-down' : 'chevron-right'} size={10} weight={3} />{t('composer.sessionInfo.sessionId')}</button>
            {shown && <dl className="session-info-rows">{info.identifiers.map((row) => <div key={row.label} className="session-info-row"><dt>{row.label}</dt><dd className="selectable">{row.value}</dd></div>)}</dl>}
          </div>
        )}
        {info.reported && <div className="session-info-foot" title={info.source ?? undefined}><div>{info.reported}</div>{info.received && <div>{info.received}</div>}</div>}
      </div>
    </div>
  );
}

/**
 * The Mighty row over the editor (M/SessionPaneView.swift composer, M/GuidedPanel.swift MightyStylePicker): the style chip — its
 * name in 10 medium, a source badge, a 7pt chevron on the subtle wash at radius 6 — and what the style is for in the tertiary ink.
 * The menu is the WinUI chip's own, read and chosen through the host.
 */
export function StyleRow({ paneId, style }: { paneId: string; style: { label: string; badge: string | null; hint: string; enabled: boolean } }) {
  const { bridge, t } = useServices();
  const [rows, setRows] = useState<MenuRow[] | null>(null);
  const close = useCallback(() => setRows(null), []);
  const ref = usePopover(close);
  usePageOverlay(rows !== null);
  const open = async () => { try { setRows(await bridge.call<MenuRow[]>('composerMenu', { paneId, menu: 'style' })); } catch { setRows(null); } };
  const choose = (path: number[]) => { setRows(null); void bridge.call('composerMenuChoose', { paneId, menu: 'style', path }).catch(() => {}); };
  return (
    <div className="composer-style-row" data-testid="composer-style-row">
      <button type="button" className="composer-style-chip" data-testid="composer-style" aria-haspopup="menu" aria-label={t('guidedPanel.stylesMenuAccessibility')} title={t('guidedPanel.stylesMenuHelp')}
        disabled={!style.enabled} onClick={() => void open()}>
        <span>{style.label}</span>{style.badge && <span className="composer-style-badge">{style.badge}</span>}<Icon name="chevron-down" size={7} weight={3} className="pill-chevron" />
      </button>
      <span className="composer-style-hint">{style.hint}</span>
      {rows && (
        <div ref={ref} className="composer-popover composer-menu" role="menu" data-menu="style" data-page-overlay="">
          <MenuList rows={rows} path={[]} choose={(path) => choose(path)} />
        </div>
      )}
    </div>
  );
}

/** One row of the 보낼 대상 menu: a target id (null for Claude) or the separator under Claude. */
export type AskRow = { kind: 'choice'; id: string | null; text: string; checked: boolean } | { kind: 'separator' };

/** The row above the editor while another CLI is the target: it runs without approvals or a sandbox (M/SessionPaneView.swift ask-unsandboxed). */
export function AskUnsandboxedRow({ target }: { target: AskTargetOption | null }) {
  const { t } = useServices();
  if (!target) return null;
  return <div className="composer-row" data-testid="composer-ask-unsandboxed" role="status">{t('composer.ask.unsandboxed', { name: target.label })}</div>;
}

/** The 보낼 대상 menu (M/SessionPaneView.swift askOptions): Claude first, a separator, then every installed CLI; the one in use is checked. */
export function askRows(targets: readonly AskTargetOption[], current: string | null, t: Translate): AskRow[] {
  return [
    { kind: 'choice', id: null, text: t('composer.ask.claude'), checked: current === null },
    { kind: 'separator' },
    ...targets.map((target): AskRow => ({ kind: 'choice', id: target.id, text: target.label, checked: current === target.id })),
  ];
}

/** Chooses where the pane's sends go (null: Claude). The host checks the pane offers it, saves it and pushes the composer again. */
export function chooseAskTarget(bridge: { call: (method: string, args?: unknown) => Promise<unknown> }, paneId: string, target: string | null): Promise<unknown> {
  return bridge.call('setAskTarget', { paneId, target });
}

/**
 * The 보낼 대상 pill (M/SessionPaneView.swift askMenu): the target asked, or Claude, tinted while another CLI is asked, with its own
 * menu. Drawn only while the pane offers a target; it stays on while the pane is busy, since the choice is the next request's.
 */
export function AskPill({ paneId, targets, current, compact = false }: { paneId: string; targets: readonly AskTargetOption[]; current: string | null; compact?: boolean }) {
  const { bridge, t } = useServices();
  const [open, setOpen] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const close = useCallback(() => setOpen(false), []);
  const ref = usePopover(close);
  usePageOverlay(open);
  if (targets.length === 0) return null;
  const asked = targets.find((target) => target.id === current) ?? null;
  const name = asked?.label ?? 'Claude';
  const choose = (target: string | null) => {
    setOpen(false);
    setProblem(null);
    chooseAskTarget(bridge, paneId, target).catch((failure: unknown) => setProblem(failure instanceof Error ? failure.message : String(failure)));
  };
  return (
    <>
      <button type="button" className={'composer-pill' + (compact ? ' compact' : '') + (asked ? ' on' : '')} data-pill="ask" data-testid="composer-ask" aria-haspopup="menu"
        aria-label={t('composer.ask.menuLabel') + ' · ' + (asked?.label ?? t('composer.ask.claude'))} title={t('composer.ask.menuLabel') + ' · ' + name} onClick={() => setOpen(!open)}>
        <Icon name="message-square-share" size={13} weight={1.75} className="pill-glyph" />{!compact && <span className="pill-text">{name}</span>}{!compact && <Icon name="chevron-down" size={8} weight={3} className="pill-chevron" />}
      </button>
      {problem && <span className="composer-pill-problem" role="alert">{problem}</span>}
      {open && (
        <div ref={ref} className="composer-popover composer-menu" role="menu" data-menu="ask" data-page-overlay="">
          {askRows(targets, asked?.id ?? null, t).map((row, index) => row.kind === 'separator' ? <div key={index} className="menu-separator" role="separator" /> : (
            <button key={index} type="button" role="menuitemradio" aria-checked={row.checked} className="menu-row" data-ask-target={row.id ?? 'claude'} onClick={() => choose(row.id)}>
              <span className="menu-check">{row.checked ? '✓' : ''}</span>{row.text}
            </button>
          ))}
        </div>
      )}
    </>
  );
}

/**
 * The composer's pills (spec §3 Menus): model, effort, permission, Codex Fast, the … pill (run settings; its context menu holds
 * the pane's actions) and the context ring (session info). Every list and every change is the host's; while the pane is busy
 * the pills are off, as in WinUI.
 */
export function Toolbar({ paneId, pills, busy, showUsage, onUsageShown, resumed = false, askTargets = [], askTarget = null }: { paneId: string; pills: Pills; busy: boolean; showUsage: boolean; onUsageShown: () => void; resumed?: boolean; askTargets?: readonly AskTargetOption[]; askTarget?: string | null }) {
  const { bridge, t } = useServices();
  const [open, setOpen] = useState<Open>(null);
  const [narrow, setNarrow] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const host = useRef<HTMLDivElement>(null);
  const close = useCallback(() => setOpen(null), []);
  const menuRef = usePopover(close);
  usePageOverlay(open !== null);

  useEffect(() => {
    const element = host.current?.parentElement;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => setNarrow((entry?.contentRect.width ?? 1000) < narrowToolbar));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const fail = (failure: unknown) => setProblem(failure instanceof Error ? failure.message : String(failure));
  const openMenu = async (name: MenuName) => {
    setProblem(null);
    try { setOpen({ kind: 'menu', name, rows: await bridge.call<MenuRow[]>('composerMenu', { paneId, menu: name }) }); } catch (failure) { fail(failure); }
  };
  const openRunSettings = async () => {
    try { setOpen({ kind: 'runSettings', info: await bridge.call<RunSettingsInfo>('runSettings', { paneId }) }); } catch (failure) { fail(failure); }
  };
  const openSessionInfo = useCallback(async () => {
    try { setOpen({ kind: 'sessionInfo', info: await bridge.call<SessionInfo>('sessionInfo', { paneId }) }); } catch (failure) { fail(failure); }
  }, [bridge, paneId]);
  useEffect(() => {
    if (!showUsage) return;
    onUsageShown();
    void openSessionInfo();
  }, [showUsage, onUsageShown, openSessionInfo]);

  const choose = (name: MenuName) => (path: number[], row: MenuRow) => {
    setOpen(null);
    if (row.kind === 'runSettings') { void openRunSettings(); return; }
    void bridge.call('composerMenuChoose', { paneId, menu: name, path }).catch(fail);
  };

  // M/ComposerControls.swift ComposerPill: a glyph, the words and a small chevron on a white capsule; the accent while it is on.
  const pill = (name: MenuName, label: ReactNode, title: string, enabled = true, on = false) => (
    <button type="button" className={'composer-pill' + (on ? ' on' : '')} data-pill={name} title={title} aria-haspopup="menu" disabled={busy || !enabled} onClick={() => void openMenu(name)}>
      {label}<Icon name="chevron-down" size={8} weight={3} className="pill-chevron" />
    </button>
  );
  const glyph = (name: IconName) => <Icon name={name} size={13} weight={1.75} className="pill-glyph" />;

  return (
    <div ref={host} className="composer-pills">
      {pill('model', <><span className="pill-glyph"><ProviderMark provider={pills.provider} size={12} /></span><span className="pill-text">{pills.model}</span></>, pills.modelTip)}
      <AskPill paneId={paneId} targets={askTargets} current={askTarget} compact={narrow} />
      {!narrow && pills.showsEffort && pill('effort', <>{glyph('brain')}<span className="pill-text">{pills.effort}</span></>, pills.effortTip, pills.effortEnabled)}
      {!narrow && pill('permission', <>{glyph(pills.unlocked ? 'lock-open' : 'shield-half')}<span className="pill-text">{pills.permission}</span></>, pills.permissionHelp, true, pills.unlocked)}
      {!narrow && pills.showsFast && (
        <button type="button" className={'composer-pill' + (pills.fast ? ' on' : '')} aria-pressed={pills.fast} title={t('composer.fast.tooltip')} disabled={busy || !pills.fastEnabled} onClick={() => void bridge.call('toggleFast', { paneId }).catch(fail)}>
          <Icon name="zap" size={13} weight={1.75} className="pill-glyph" fill={pills.fast} /><span className="pill-text">Fast</span>
        </button>
      )}
      {narrow ? (
        <button type="button" className={'composer-pill compact' + (pills.unlocked || pills.fast ? ' on' : '')} data-pill="options" aria-haspopup="menu" disabled={busy}
          title={t('composer.effort.label') + ' · ' + t('composer.label.permission') + ' · ' + t('settings.run.title')} onClick={() => void openMenu('options')}><Icon name="sliders-horizontal" size={13} weight={1.75} /></button>
      ) : (
        <button type="button" className={'composer-pill compact' + (pills.moreOn ? ' on' : '')} data-pill="more" aria-label={t('composer.more')} title={t('settings.run.title')} onClick={() => void openRunSettings()} onContextMenu={(event) => { event.preventDefault(); void openMenu('actions'); }}><Icon name="ellipsis" size={14} weight={2.4} /></button>
      )}
      <span className="composer-pills-end">
        {resumed && <span className="composer-resume" title={t('composer.resume.help')} aria-label={t('composer.resume.name')}><Icon name="split" size={13} weight={1.75} /></span>}
        <button type="button" className="context-ring" data-testid="context-ring" aria-label={t('composer.sessionInfo.title') + ' · ' + contextLabel(pills.contextPercent)} title={t(pills.contextPercent === null ? 'composer.context.unavailable' : 'composer.context.tooltip')} data-high={pills.contextPercent !== null && pills.contextPercent >= 95 ? '' : undefined} onClick={() => void openSessionInfo()}>
          <ContextRing percent={pills.contextPercent} size={24} />
        </button>
      </span>
      {problem && <span className="composer-pill-problem" role="alert">{problem}</span>}
      {open?.kind === 'menu' && (
        <div ref={menuRef} className="composer-popover composer-menu" role="menu" data-menu={open.name} data-page-overlay="">
          <MenuList rows={open.rows} path={[]} choose={choose(open.name)} />
        </div>
      )}
      {open?.kind === 'runSettings' && <RunSettingsPopover paneId={paneId} info={open.info} busy={busy} close={close} />}
      {open?.kind === 'sessionInfo' && <SessionInfoPopover info={open.info} close={close} />}
    </div>
  );
}
