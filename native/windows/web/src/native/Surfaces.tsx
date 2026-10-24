// The host's WinUI views drawn over the page (MainWindow.WebShell.Settings.cs): Settings with its categories, the setup screen,
// the dialogs and sheets of the window's flows (plugins, style approval, terminal history, model id, confirmations) and the
// companion popover. The newest is on top and takes Esc; every input goes to the host through `nativeAction`.
import { useEffect, useMemo, useRef, type KeyboardEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { BetaBadge } from '../shell/parts';
import { ActContext, NativeNode, type Act } from './NativeNode';
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

function Settings({ open, view, call }: { open: NativeOpen; view: NativeView; call: (method: string, args?: object) => void }) {
  const form = useRef<HTMLDivElement>(null);
  // A new category starts at its top, as the WinUI form's scroll does.
  useEffect(() => { form.current?.scrollTo({ top: 0 }); }, [view.selected]);
  return (
    <div className="native-settings" style={{ width: view.width ?? 800, height: view.height ?? 700 }}>
      <header className="native-settings-head">
        <span className="native-glyph native-settings-gear" aria-hidden="true">{''}</span>
        <div><h2 className="sheet-title">{view.title}</h2>{view.subtitle && <div className="quiet small">{view.subtitle}</div>}</div>
        <span className="spacer" />
        <button type="button" className="native-close" aria-label={view.close} title={view.close} onClick={() => call('nativeClose', { view: open.id })}>×</button>
      </header>
      <div className="native-settings-body">
        <nav className="native-settings-nav" data-testid="settings-nav">
          {view.nav?.map((item) => (
            <button key={item.id} type="button" className={'native-nav-row' + (item.id === view.selected ? ' selected' : '')} aria-current={item.id === view.selected ? 'page' : undefined}
              data-category={item.id} onClick={() => call('nativeNav', { view: open.id, id: item.id })}>
              <span className="native-glyph" aria-hidden="true">{item.glyph}</span>{item.title}
            </button>
          ))}
        </nav>
        <div className="native-settings-form" ref={form} data-testid="settings-form" data-category={view.selected}>
          <Sections sections={view.sections} framed />
        </div>
      </div>
      <footer className="native-settings-foot">
        <span className="spacer" />
        <button type="button" className="plain-button" data-testid="settings-close" onClick={() => call('nativeClose', { view: open.id })}>{view.close}</button>
      </footer>
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
