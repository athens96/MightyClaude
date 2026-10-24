// Small pieces the frame draws in many places: the status glyph (MightyClaude.Core/StatusGlyph.cs tones), the beta capsule,
// the status counts (StatusCountsView), the Git capsule and one context menu at a time.
import { createContext, useCallback, useContext, useEffect, useRef, useState, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react';
import { useServices, useStoreState } from '../app/context';
import type { Counts, GitInfo, PaneInfo, ShellModel, Tone } from './types';
import { usePageOverlay } from './overlay';
import { Icon, type IconName } from '../icons/Icon';

/** The frame's model, or null before the host sent it. */
export function useShell(): ShellModel | null {
  return useStoreState().shell;
}

/** Each pane's info by id. */
export function usePaneInfo(): Map<string, PaneInfo> {
  const shell = useShell();
  const ref = useRef<{ list: PaneInfo[] | undefined; map: Map<string, PaneInfo> }>({ list: undefined, map: new Map() });
  if (ref.current.list !== shell?.panes) ref.current = { list: shell?.panes, map: new Map((shell?.panes ?? []).map((p) => [p.id, p])) };
  return ref.current.map;
}

/** Calls a host method; its failure is the host's error banner, not the page's. */
export function useCall(): (method: string, args?: unknown) => void {
  const { bridge } = useServices();
  return useCallback((method: string, args?: unknown) => void bridge.call(method, args).catch(() => {}), [bridge]);
}

/** The symbol an idle pane that is not an agent's shows in place of the ring (M/DashboardView.swift paneSymbol). */
function paneSymbol(kind: string): IconName {
  if (kind === 'browser' || kind === 'agent-browser') return 'globe';
  if (kind === 'files') return 'folder';
  return 'square-terminal';
}

/**
 * A tone's glyph (M/StatusGlyph.swift, Core StatusGlyphKind, on its 16-unit grid): the turning spark while running, the amber
 * "?" disc, a check, a slashed ring, the red "!" disc, a small ring for an idle agent and the pane's own symbol for an idle pane
 * that is not one. Inks are the tokens' derived glyph and disc colours.
 */
export function StatusGlyph({ tone, size = 14, kind = 'claude' }: { tone: Tone; size?: number; kind?: string }) {
  const line = (width: number) => ({ stroke: 'currentColor', strokeWidth: width, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, fill: 'none' });
  if (tone === 'idle' && kind !== 'claude') return <span className="status-glyph tone-idle" data-tone={tone} style={{ width: size, height: size }}><Icon name={paneSymbol(kind)} size={size} weight={2} /></span>;
  const body = (() => {
    switch (tone) {
      case 'run': return (
        <g className="glyph-spark">
          <path d="M8 1.6v4M8 10.4v4M1.6 8h4M10.4 8h4" {...line(2)} />
          <path d="M4.6 4.6l1.5 1.5M9.9 9.9l1.5 1.5M4.6 11.4l1.5-1.5M9.9 6.1l1.5-1.5" {...line(2)} opacity={0.7} />
        </g>
      );
      case 'wait': return <><circle cx="8" cy="8" r="7.2" fill="var(--disc-wait)" /><path d="M5.9 6.11A2.1 2.1 0 1 1 8.8 8.05C8.3 8.27 8 8.65 8 9.2L8 9.55" fill="none" stroke="var(--disc-wait-ink)" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /><circle cx="8" cy="11.7" r="1.05" fill="var(--disc-wait-ink)" /></>;
      case 'err': return <><circle cx="8" cy="8" r="7.2" fill="var(--disc-err)" /><path d="M8 4.3v4.6" stroke="var(--disc-err-ink)" strokeWidth="1.9" strokeLinecap="round" /><circle cx="8" cy="11.6" r="1.05" fill="var(--disc-err-ink)" /></>;
      case 'done': return <path d="M3.2 8.5l3 3 6.6-7" {...line(1.8)} />;
      case 'stop': return <><circle cx="8" cy="8" r="6" {...line(1.8)} /><path d="M3.9 12.1l8.2-8.2" {...line(1.8)} /></>;
      default: return <circle cx="8" cy="8" r="3.6" {...line(1.8)} />;
    }
  })();
  return <svg className={'status-glyph tone-' + tone} width={size} height={size} viewBox="0 0 16 16" aria-hidden="true" focusable="false" data-tone={tone}>{body}</svg>;
}

export function BetaBadge() {
  const { t } = useServices();
  return <span className="beta-badge" aria-label={t('badge.betaAccessibility')}>{t('badge.beta')}</span>;
}

/** StatusCountsView: the short form (waiting, running, error) or the long form with the settled states and their words. */
export function StatusCounts({ counts, long = false }: { counts: Counts; long?: boolean }) {
  const { t } = useServices();
  const entries: [Tone, number, string][] = [
    ['wait', counts.questions + counts.permissions, 'phone.dashboard.stat.waiting'],
    ['run', counts.running, 'session.state.running'],
    ['err', counts.errors, 'session.state.error'],
  ];
  if (long) entries.push(['done', counts.done, 'session.state.completed'], ['stop', counts.stopped, 'session.state.stopped'], ['idle', counts.idle, 'session.state.idle']);
  const shown = entries.filter(([, count]) => count > 0);
  if (shown.length === 0) return null;
  return (
    <span className="status-counts">
      {shown.map(([tone, count, word]) => (
        <span key={tone} className="status-count" data-count-tone={tone} title={t('phone.dashboard.statLabel', { label: t(word), count })}>
          <StatusGlyph tone={tone} size={12} />
          <span className="status-count-number">{count}</span>
          {long && <span className="status-count-word">{t(word)}</span>}
        </span>
      ))}
    </span>
  );
}

export function GitCapsule({ git }: { git: GitInfo }) {
  return (
    <span className="git-capsule" title={git.help} aria-label={git.help} data-testid="git-capsule">
      <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><circle cx="2.6" cy="1.9" r="1.3" fill="none" stroke="currentColor" /><circle cx="2.6" cy="8.1" r="1.3" fill="none" stroke="currentColor" /><circle cx="7.4" cy="2.9" r="1.3" fill="none" stroke="currentColor" /><path d="M2.6 3.2v3.6M7.4 4.2v.7L5.9 6.1H2.6" fill="none" stroke="currentColor" /></svg>
      <span className="git-label">{git.label}</span>
      {git.dirty && <span className="git-dirty" />}
      {git.ahead > 0 && <span>↑{git.ahead}</span>}
      {git.behind > 0 && <span>↓{git.behind}</span>}
    </span>
  );
}

/** A path cut in its middle when it does not fit, as the Mac's .truncationMode(.middle): the browser can only cut its end, so two halves. */
export function MiddlePath({ path, className }: { path: string; className?: string }) {
  const half = Math.ceil(path.length / 2);
  return (
    <span className={'middle-path ' + (className ?? '')} title={path}>
      <span className="middle-path-head">{path.slice(0, half)}</span>
      <span className="middle-path-tail"><bdi dir="ltr">{path.slice(half)}</bdi></span>
    </span>
  );
}

// ── Menus ──

export interface MenuItem {
  label?: string;
  action?: () => void;
  disabled?: boolean;
  separator?: boolean;
  icon?: ReactNode;
  shortcut?: string;
  testId?: string;
}

interface OpenMenu {
  x: number;
  y: number;
  items: MenuItem[];
}

const MenuContext = createContext<(at: { x: number; y: number }, items: MenuItem[]) => void>(() => {});

/** Opens a menu at the pointer (a right-click) or under an element (a button). */
export function useMenu(): { atPointer: (event: ReactMouseEvent, items: MenuItem[]) => void; under: (element: Element, items: MenuItem[]) => void } {
  const open = useContext(MenuContext);
  return {
    atPointer: (event, items) => { event.preventDefault(); event.stopPropagation(); open({ x: event.clientX, y: event.clientY }, items); },
    under: (element, items) => { const box = element.getBoundingClientRect(); open({ x: box.left, y: box.bottom + 2 }, items); },
  };
}

export function MenuHost({ children }: { children: ReactNode }) {
  const [menu, setMenu] = useState<OpenMenu | null>(null);
  const element = useRef<HTMLDivElement>(null);
  const open = useCallback((at: { x: number; y: number }, items: MenuItem[]) => setMenu({ ...at, items }), []);
  usePageOverlay(menu !== null);
  useEffect(() => {
    if (!menu) return;
    const close = (event: Event) => { if (!(event.target instanceof Node && element.current?.contains(event.target))) setMenu(null); };
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') setMenu(null); };
    window.addEventListener('mousedown', close, true); window.addEventListener('keydown', key, true); window.addEventListener('blur', () => setMenu(null), { once: true });
    element.current?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
    return () => { window.removeEventListener('mousedown', close, true); window.removeEventListener('keydown', key, true); };
  }, [menu]);
  // Kept inside the window: a menu opened near the right or bottom edge opens toward the middle.
  const style = menu ? { left: Math.min(menu.x, window.innerWidth - 240), top: Math.min(menu.y, window.innerHeight - 32 * menu.items.length - 8) } : undefined;
  return (
    <MenuContext.Provider value={open}>
      {children}
      {menu && (
        <div ref={element} className="menu" role="menu" style={style} data-testid="menu" data-page-overlay="">
          {menu.items.map((item, index) =>
            item.separator ? <div key={index} className="menu-separator" role="separator" /> : (
              <button key={index} type="button" role="menuitem" className="menu-item" disabled={item.disabled} data-testid={item.testId}
                onClick={() => { setMenu(null); item.action?.(); }}>
                <span className="menu-icon">{item.icon}</span>
                <span className="menu-label">{item.label}</span>
                {item.shortcut && <span className="menu-shortcut">{item.shortcut}</span>}
              </button>
            ))}
        </div>
      )}
    </MenuContext.Provider>
  );
}
