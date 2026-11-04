// The host's WinUI views drawn over the page (MainWindow.WebShell.Settings.cs): Settings with its categories, the setup screen,
// the dialogs and sheets of the window's flows (plugins, style approval, terminal history, model id, confirmations) and the
// companion popover. The newest is on top and takes Esc; every input goes to the host through `nativeAction`. Each is drawn
// in the popup standard (dialogs/DialogShell.tsx): Settings and the setup screen are panels, a dialog is the alert, form sheet
// or panel its chrome names, and the companion is a popover.
import { useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import tokens from '@tokens/design-tokens.json';
import { useServices, useStoreState } from '../app/context';
import { BetaBadge } from '../shell/parts';
import { ActContext, NativeNode, type Act } from './NativeNode';
import { Icon, type IconName } from '../icons/Icon';
import { CloseButton, DialogFooter, DialogFrame, DialogHeader, useFitToggle } from '../dialogs/DialogShell';
import { lucide } from '../icons/lucide';
import { usePopover } from '../shell/popover';
import type { NativeButton, NativeChrome, NativeNode as NativeNodeT, NativeOpen, NativeSection, NativeView } from './types';

/** Each category's symbol in the Mac's sidebar (M/SettingsViews.swift SettingsPane.symbol), as its Lucide counterpart. */
const categorySymbol: Record<string, IconName> = {
  general: 'settings', models: 'cpu', styles: 'palette', tools: 'box', cli: 'square-terminal', mobile: 'smartphone', remote: 'earth', companion: 'paw-print', about: 'info',
};

const settings = tokens.metrics.settings;

/** Whether a node draws anything: a stack, grid or box only when one of its parts does, a text only when it has words. */
export function shows(node: NativeNodeT | null | undefined): boolean {
  if (!node) return false;
  if (node.t === 'stack' || node.t === 'grid' || node.t === 'box') return node.c.some(shows);
  if (node.t === 'text') return node.text.trim().length > 0;
  return true;
}

function Sections({ sections, framed }: { sections: readonly NativeSection[]; framed: boolean }) {
  return (
    <>
      {sections.map((section, index) => (
        <section key={(section.title ?? '') + index} className="native-section" data-testid="native-section" data-title={section.title ?? undefined}>
          {section.title && <h3 className="native-section-title">{section.title}{section.beta && <BetaBadge />}</h3>}
          {section.node && <div className={framed ? 'native-section-box' : undefined}><NativeNode node={section.node} /></div>}
        </section>
      ))}
    </>
  );
}

/**
 * The categories' cards in the One UI list: related categories share one rounded card (setup and models, the
 * workspace's tools, the connections, the rest); a category none of them names joins the last card.
 */
const categoryGroups: readonly (readonly string[])[] = [['general', 'models', 'styles'], ['tools', 'cli'], ['mobile', 'remote'], ['companion', 'about']];

/** Each category's icon tile, in one of the palette's own tones (no colour outside the design tokens). */
const categoryTone: Record<string, string> = {
  general: 'var(--stop)', models: 'var(--agent)', styles: 'var(--err)', tools: 'var(--wait)', cli: 'var(--ink3)',
  mobile: 'var(--done)', remote: 'var(--task)', companion: 'var(--run)', about: 'var(--accent)',
};

export function groupCategories<T extends { id: string }>(items: readonly T[]): T[][] {
  const known = new Set(categoryGroups.flat());
  const groups = categoryGroups.map((ids) => items.filter((item) => ids.includes(item.id)));
  const rest = items.filter((item) => !known.has(item.id));
  if (rest.length) groups[groups.length - 1] = [...groups[groups.length - 1]!, ...rest];
  return groups.filter((group) => group.length > 0);
}

/** How far a pane has scrolled under its large title: 0 at the top, 1 once the title has gone under the bar. */
function useCollapse(element: RefObject<HTMLDivElement | null>, reset: unknown, distance: number): number {
  const [progress, setProgress] = useState(0);
  useEffect(() => {
    const scroller = element.current;
    if (!scroller) return undefined;
    const update = () => setProgress(Math.min(1, scroller.scrollTop / distance));
    update();
    scroller.addEventListener('scroll', update, { passive: true });
    return () => scroller.removeEventListener('scroll', update);
  }, [element, reset, distance]);
  return progress;
}

/** A pane's bar: its title (shown once the large one has scrolled away) and the window's ✕; its double-click fits the window. */
function SettingsBar({ title, shown, onClose, closeLabel }: { title?: string; shown: boolean; onClose?: () => void; closeLabel?: string }) {
  const fit = useFitToggle();
  return (
    <div className={'native-settings-bar' + (shown ? ' collapsed' : '')} onDoubleClick={(event) => { if (fit && !(event.target as Element).closest('button')) fit(); }}>
      <span className="native-settings-bar-title" style={{ opacity: shown ? 1 : 0 }}>{title}</span>
      {onClose && <span className="native-settings-close" title={closeLabel}><CloseButton onClose={onClose} testId="settings-close" /></span>}
    </div>
  );
}

/**
 * Settings (metrics.settings, the same numbers as the Mac's): 860 × 640 (at least 720 × 520, resizable, its size kept), the
 * category list 260 wide under its bar title, and the page opening on its large title (28, a 104 hero) that gives way to the
 * bar's (15) as it scrolls, with the ✕ (28/14) at the bar's end. The list groups the categories into rounded cards with a 30
 * tile per category; the page shows each section as a radius-16 card under its title in the accent, its rows the one row
 * pattern (native.css). Every control is the host's (NativeNode), restyled only.
 */
function Settings({ open, view, call, top }: { open: NativeOpen; view: NativeView; call: (method: string, args?: object) => void; top: boolean }) {
  const form = useRef<HTMLDivElement>(null);
  // A new category starts at its top, as the WinUI form's scroll does.
  useEffect(() => { form.current?.scrollTo({ top: 0 }); }, [view.selected]);
  const formScrolled = useCollapse(form, view.selected, settings.heroHeight - tokens.metrics.dialog.headerHeight);
  const current = view.nav?.find((item) => item.id === view.selected);
  const close = () => call('nativeClose', { view: open.id });
  return (
    <DialogFrame kind="panel" panel="settings" width={settings.width} height={settings.height} minWidth={settings.minWidth} minHeight={settings.minHeight}
      label={view.title} top={top} focus="frame" layer="surface" onCancel={close} className="native-settings" testId="native-settings"
      backdrop={{ 'data-testid': 'native-view', 'data-native-view': open.id, 'data-kind': view.kind }}>
      <div className="native-settings-list">
        <div className="native-settings-list-bar">{view.title}</div>
        <nav className="native-settings-nav" data-testid="settings-nav">
          {groupCategories(view.nav ?? []).map((group, index) => (
            <div key={index} className="native-settings-card">
              {group.map((item) => (
                <button key={item.id} type="button" className={'native-nav-row' + (item.id === view.selected ? ' selected' : '')} aria-current={item.id === view.selected ? 'page' : undefined}
                  data-category={item.id} onClick={() => call('nativeNav', { view: open.id, id: item.id })}>
                  <span className="native-nav-tile" aria-hidden="true" style={{ background: categoryTone[item.id] ?? 'var(--accent)' }}>
                    {categorySymbol[item.id] ? <Icon name={categorySymbol[item.id]!} size={settings.navGlyph} weight={2} /> : <span className="native-glyph">{item.glyph}</span>}
                  </span>
                  <span className="native-nav-title">{item.title}</span>
                </button>
              ))}
            </div>
          ))}
        </nav>
      </div>
      <div className="native-settings-page">
        <SettingsBar title={current?.title} shown={formScrolled > 0.85} onClose={close} closeLabel={view.close} />
        <div className="native-settings-form" ref={form} data-testid="settings-form" data-category={view.selected}>
          <header className="native-settings-hero" style={{ opacity: 1 - formScrolled }}><h2 className="native-settings-title">{current?.title}</h2></header>
          <Sections sections={view.sections} framed />
        </div>
      </div>
    </DialogFrame>
  );
}

/** The footer's buttons in the standard's order: [secondary] [cancel] [primary], the primary at the far right. */
export function orderedButtons(buttons: readonly NativeButton[], panel: boolean): NativeButton[] {
  const rank = (button: NativeButton) => button.id === 'secondary' ? 0 : button.id === 'close' ? 1 : 2;
  // A panel closes with its ✕: its close button is not drawn again at the bottom.
  return buttons.filter((button) => !(panel && button.id === 'close')).sort((a, b) => rank(a) - rank(b));
}

const iconName = (name: string | null | undefined): IconName | null => name && name in lucide ? name as IconName : null;

/** A dialog of the window's flows, the setup screen: an alert, a form sheet or a panel, as its chrome says. */
function DialogSurface({ open, view, call, top }: { open: NativeOpen; view: NativeView; call: (method: string, args?: object) => void; top: boolean }) {
  const chrome: NativeChrome = view.chrome ?? (view.kind === 'setup' ? { kind: 'panel', panel: 'setup' } : { kind: 'sheet' });
  const panel = chrome.kind === 'panel';
  const close = () => call('nativeClose', { view: open.id });
  const icon = iconName(chrome.icon);
  const buttons = orderedButtons(view.buttons ?? [], panel);
  // The setup screen's Later is its ✕ now; its never-show box stays at the footer's left. A status or action whose parts are all
  // hidden (the plugins' ring and cancel while nothing runs) counts as none, so an idle panel has no footer (standard decision 10).
  const statusNode = shows(chrome.status) ? chrome.status : null, footerNode = shows(view.footer) ? view.footer : null, actionsNode = shows(chrome.actions) ? chrome.actions : null;
  const status = (statusNode || footerNode) ? <>{statusNode && <NativeNode node={statusNode} />}{footerNode && <NativeNode node={footerNode} />}</> : null;
  return (
    <DialogFrame kind={chrome.kind} panel={chrome.panel ?? undefined} width={view.width ?? (view.kind === 'setup' ? 620 : undefined)} height={view.height}
      minWidth={chrome.minWidth} minHeight={chrome.minHeight} label={view.title} busy={chrome.busy} top={top} onCancel={close}
      layer={open.place === 'surface' ? 'surface' : 'dialog'} className="native-sheet"
      backdrop={{ 'data-testid': 'native-view', 'data-native-view': open.id, 'data-kind': view.kind, 'data-dialog-kind': chrome.kind }}>
      {(view.title || chrome.subtitle || view.subtitle || panel) && (
        <DialogHeader title={view.title} subtitle={chrome.subtitle ?? view.subtitle} icon={icon && <Icon name={icon} size={18} weight={1.8} />}
          tools={chrome.tools && <NativeNode node={chrome.tools} />} onClose={panel ? close : undefined} busy={chrome.busy} closeTestId="native-close" />
      )}
      <div className="dialog-body native-sheet-body"><Sections sections={view.sections} framed={false} /></div>
      <DialogFooter status={status}>
        {actionsNode && <NativeNode node={actionsNode} />}
        {buttons.length > 0 && buttons.map((button) => (
          <button key={button.id} type="button" className={'btn' + (button.id === 'primary' ? chrome.danger ? ' danger' : ' primary' : button.accent ? ' primary' : '')} disabled={button.disabled}
            data-testid={'native-button-' + button.id} onClick={() => call('nativeButton', { view: open.id, button: button.id })}>{button.label}</button>
        ))}
      </DialogFooter>
    </DialogFrame>
  );
}

/** The companion's status (kind D): a narrow popover over the status bar; a press outside it or Esc closes it. */
function PopoverSurface({ open, view, call }: { open: NativeOpen; view: NativeView; call: (method: string, args?: object) => void }) {
  const ref = usePopover(() => call('nativeClose', { view: open.id }));
  return (
    <div className="native-popover-place" data-testid="native-view" data-native-view={open.id} data-kind={view.kind}>
      <div ref={ref} className="popover width-s native-popover" role="dialog" aria-label={view.title}>
        {view.title && <div className="popover-head"><div className="dialog-heading"><strong>{view.title}</strong>{view.subtitle && <span className="dialog-subtitle">{view.subtitle}</span>}</div></div>}
        <div className="native-popover-body"><Sections sections={view.sections} framed={false} /></div>
      </div>
    </div>
  );
}

function Surface({ open, top }: { open: NativeOpen; top: boolean }) {
  const { bridge } = useServices();
  const call = useMemo(() => (method: string, args?: object) => void bridge.call(method, args).catch(() => {}), [bridge]);
  const act = useMemo<Act>(() => (id, op, value) => call('nativeAction', { view: open.id, id, op, value }), [call, open.id]);
  const { view, place } = open;
  return (
    <ActContext.Provider value={act}>
      {view.kind === 'settings' ? <Settings open={open} view={view} call={call} top={top} />
        : place === 'popover' ? <PopoverSurface open={open} view={view} call={call} />
        : <DialogSurface open={open} view={view} call={call} top={top} />}
    </ActContext.Provider>
  );
}

/** Every open view, oldest first, so the newest lies on top. */
export function Surfaces() {
  const natives = useStoreState().natives;
  const { bridge } = useServices();
  // Views open before the page loaded (or loaded again) are sent again.
  useEffect(() => { void bridge.call('nativeState').catch(() => {}); }, [bridge]);
  return <>{natives.map((open, index) => <Surface key={open.id} open={open} top={index === natives.length - 1} />)}</>;
}
