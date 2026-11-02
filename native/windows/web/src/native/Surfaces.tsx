// The host's WinUI views drawn over the page (MainWindow.WebShell.Settings.cs): Settings with its categories, the setup screen,
// the dialogs and sheets of the window's flows (plugins, style approval, terminal history, model id, confirmations) and the
// companion popover. The newest is on top and takes Esc; every input goes to the host through `nativeAction`.
import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type RefObject } from 'react';
import { useServices, useStoreState } from '../app/context';
import { BetaBadge } from '../shell/parts';
import { ActContext, NativeNode, SettingsFormContext, type Act } from './NativeNode';
import { Icon, type IconName } from '../icons/Icon';

/** Each category's symbol in the Mac's sidebar (M/SettingsViews.swift SettingsPane.symbol), as its Lucide counterpart. */
const categorySymbol: Record<string, IconName> = {
  general: 'settings', models: 'cpu', styles: 'palette', tools: 'box', cli: 'square-terminal', mobile: 'smartphone', remote: 'earth', companion: 'paw-print', about: 'info',
};
import type { NativeOpen, NativeSection, NativeView } from './types';

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
function useCollapse(element: RefObject<HTMLDivElement | null>, reset: unknown): number {
  const [progress, setProgress] = useState(0);
  useEffect(() => {
    const scroller = element.current;
    if (!scroller) return undefined;
    const update = () => setProgress(Math.min(1, scroller.scrollTop / 120));
    update();
    scroller.addEventListener('scroll', update, { passive: true });
    return () => scroller.removeEventListener('scroll', update);
  }, [element, reset]);
  return progress;
}

/**
 * Settings in the One UI layout: two panes, each opening on a large title that gives way to a small one in its bar as the
 * pane scrolls. The list groups the categories into rounded cards with a tile per category; the page shows each section
 * as a rounded card under its title in the accent. Every control is the host's (NativeNode), restyled only.
 */
function Settings({ open, view, call }: { open: NativeOpen; view: NativeView; call: (method: string, args?: object) => void }) {
  const form = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLDivElement>(null);
  // A new category starts at its top, as the WinUI form's scroll does.
  useEffect(() => { form.current?.scrollTo({ top: 0 }); }, [view.selected]);
  const formScrolled = useCollapse(form, view.selected);
  const listScrolled = useCollapse(list, null);
  const current = view.nav?.find((item) => item.id === view.selected);
  const close = () => call('nativeClose', { view: open.id });
  return (
    <div className="native-settings" data-testid="native-settings" style={{ width: view.width ?? 800, height: view.height ?? 700 }}>
      <div className="native-settings-list">
        <div className="native-settings-bar"><span className="native-settings-bar-title" style={{ opacity: listScrolled > 0.85 ? 1 : 0 }}>{view.title}</span></div>
        <div className="native-settings-scroll" ref={list}>
          <header className="native-settings-hero" style={{ opacity: 1 - listScrolled }}>
            <h2 className="native-settings-title">{view.title}</h2>
            {view.subtitle && <div className="native-settings-subtitle">{view.subtitle}</div>}
          </header>
          <nav className="native-settings-nav" data-testid="settings-nav">
            {groupCategories(view.nav ?? []).map((group, index) => (
              <div key={index} className="native-settings-card">
                {group.map((item) => (
                  <button key={item.id} type="button" className={'native-nav-row' + (item.id === view.selected ? ' selected' : '')} aria-current={item.id === view.selected ? 'page' : undefined}
                    data-category={item.id} onClick={() => call('nativeNav', { view: open.id, id: item.id })}>
                    <span className="native-nav-tile" aria-hidden="true" style={{ background: categoryTone[item.id] ?? 'var(--accent)' }}>
                      {categorySymbol[item.id] ? <Icon name={categorySymbol[item.id]!} size={17} weight={2} /> : <span className="native-glyph">{item.glyph}</span>}
                    </span>
                    <span className="native-nav-title">{item.title}</span>
                  </button>
                ))}
              </div>
            ))}
          </nav>
        </div>
      </div>
      <div className="native-settings-page">
        <div className="native-settings-bar">
          <span className="native-settings-bar-title" style={{ opacity: formScrolled > 0.85 ? 1 : 0 }}>{current?.title}</span>
          <span className="spacer" />
          <button type="button" className="native-settings-close" data-testid="settings-close" aria-label={view.close} title={view.close} onClick={close}><Icon name="x" size={18} weight={1.8} /></button>
        </div>
        <div className="native-settings-form" ref={form} data-testid="settings-form" data-category={view.selected}>
          <header className="native-settings-hero" style={{ opacity: 1 - formScrolled }}><h2 className="native-settings-title">{current?.title}</h2></header>
          <SettingsFormContext.Provider value={true}><Sections sections={view.sections} framed /></SettingsFormContext.Provider>
        </div>
      </div>
    </div>
  );
}

function Surface({ open, top }: { open: NativeOpen; top: boolean }) {
  const { bridge } = useServices();
  const call = useMemo(() => (method: string, args?: object) => void bridge.call(method, args).catch(() => {}), [bridge]);
  const act = useMemo<Act>(() => (id, op, value) => call('nativeAction', { view: open.id, id, op, value }), [call, open.id]);
  const element = useRef<HTMLDivElement>(null);
  const { view, place } = open;
  useEffect(() => {
    if (!top) return;
    const focusable = element.current?.querySelector<HTMLElement>('button.accent, .native-buttons .accent-button, input, select, textarea, button');
    focusable?.focus({ preventScroll: true });
  }, [top]);
  const key = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || !top) return;
    event.preventDefault(); event.stopPropagation();
    call('nativeClose', { view: open.id });
  };
  const body = view.kind === 'settings' ? <Settings open={open} view={view} call={call} />
    : (
      <div className={'native-sheet ' + place} style={{ width: view.width ?? (view.kind === 'setup' ? 620 : 440), maxHeight: view.height ?? undefined }}>
        {(view.title || view.subtitle) && (
          <header>
            {view.title && <h2 className="sheet-title">{view.title}</h2>}
            {view.subtitle && <div className="quiet small">{view.subtitle}</div>}
          </header>
        )}
        <div className="native-sheet-body"><Sections sections={view.sections} framed={false} /></div>
        {(view.footer || view.close || view.buttons?.length) && (
          <footer className="sheet-buttons native-buttons">
            {view.footer && <NativeNode node={view.footer} />}
            <span className="spacer" />
            {view.close && place !== 'dialog' && <button type="button" className="plain-button" data-testid="native-close" onClick={() => call('nativeClose', { view: open.id })}>{view.close}</button>}
            {view.buttons?.map((button) => (
              <button key={button.id} type="button" className={button.id === 'primary' || button.accent ? 'accent-button' : 'plain-button'} disabled={button.disabled}
                data-testid={'native-button-' + button.id} onClick={() => call('nativeButton', { view: open.id, button: button.id })}>{button.label}</button>
            ))}
          </footer>
        )}
      </div>
    );
  return (
    <ActContext.Provider value={act}>
      <div className={'native-backdrop ' + place + (top ? ' top' : '')} data-testid="native-view" data-native-view={open.id} data-kind={view.kind} ref={element} onKeyDown={key}
        onPointerDown={(event) => { if (place === 'popover' && event.target === event.currentTarget) call('nativeClose', { view: open.id }); }}>
        <div role="dialog" aria-modal={place !== 'popover'} aria-label={view.title} className="native-frame">{body}</div>
      </div>
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
