import { useContext, useEffect, useRef, useState, type KeyboardEvent } from 'react';
import type { RunSession } from '../../store/models';
import { useServices } from '../../app/context';
import { PaneShownContext } from '../../pane/shown';
import { listenPane } from '../channel';
import { sameBounds, slotBounds, type BrowserState, type SlotBounds } from './types';

/**
 * A browser pane (M/BrowserPaneView.swift:30-108; WinUI MainWindow.Browser.cs): the toolbar (back, forward, reload, the address
 * field) over a slot the host's own WebView2 covers. The page reports the slot in device pixels whenever it may have moved, and
 * as hidden while the pane is behind another tab, a dialog is open or the pane is gone, so the native browser never stands over
 * something else. Every rule (address, history, profile, refusals) is the host's (Core BrowserAddress, BrowserHistory, BrowserProfile).
 */
export function BrowserView({ session }: { session: RunSession }) {
  const { bridge, store, t } = useServices();
  const shown = useContext(PaneShownContext);
  const paneId = session.id;
  const slot = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<BrowserState>({ status: 'starting', canGoBack: false, canGoForward: false });
  const [address, setAddress] = useState('');
  const editing = useRef(false);

  useEffect(() => {
    const stop = listenPane('browser', paneId, (message) => setState(message.state));
    void bridge.call<BrowserState>('browserOpen', { paneId }).then((value) => { if (value) setState(value); }, () => {});
    return stop;
  }, [bridge, paneId]);

  useEffect(() => {
    if (!editing.current) setAddress(state.url ?? '');
  }, [state.url]);

  // The slot's bounds: measured on the next frame after anything that can move it, sent only when they changed.
  useEffect(() => {
    const element = slot.current;
    if (!element) return;
    let last: SlotBounds | undefined;
    let frame = 0;
    const measure = () => {
      frame = 0;
      const current = store.getState();
      const blocked = current.dialogs.length > 0 || (current.shell?.dialogOpen ?? false);
      const next = slotBounds(element.getBoundingClientRect(), window.devicePixelRatio || 1, shown && !blocked && element.isConnected);
      if (sameBounds(last, next)) return;
      last = next;
      void bridge.call('browserBounds', { paneId, ...next }).catch(() => {});
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(measure); };
    const observer = new ResizeObserver(schedule);
    observer.observe(element);
    observer.observe(document.documentElement);
    // Only the frame's model and the dialogs move or cover the slot; a streamed reply elsewhere does not.
    let seen = store.getState();
    const unsubscribe = store.subscribe(() => {
      const current = store.getState();
      if (current.shell !== seen.shell || current.dialogs !== seen.dialogs || current.chosen !== seen.chosen) schedule();
      seen = current;
    });
    window.addEventListener('resize', schedule);
    schedule();
    return () => {
      if (frame) cancelAnimationFrame(frame);
      observer.disconnect();
      unsubscribe();
      window.removeEventListener('resize', schedule);
      void bridge.call('browserBounds', { paneId, x: 0, y: 0, width: 0, height: 0, visible: false }).catch(() => {});
    };
  }, [bridge, store, paneId, shown, state.status]);

  const ready = state.status === 'ready';
  const act = (method: string, args?: object) => void bridge.call(method, { paneId, ...args }).catch(() => {});
  const keyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'Enter' || event.nativeEvent.isComposing) return;
    event.preventDefault();
    act('browserNavigate', { address });
    event.currentTarget.blur();
  };
  return (
    <div className="browser-pane" data-testid="browser-pane">
      <div className="browser-toolbar">
        <button type="button" className="browser-nav" disabled={!ready || !state.canGoBack} aria-label={t('browser.back')} title={t('browser.back')} onClick={() => act('browserBack')}>
          <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M7.5 2 3.5 6l4 4" /></svg>
        </button>
        <button type="button" className="browser-nav" disabled={!ready || !state.canGoForward} aria-label={t('browser.forward')} title={t('browser.forward')} onClick={() => act('browserForward')}>
          <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M4.5 2 8.5 6l-4 4" /></svg>
        </button>
        <button type="button" className="browser-nav" disabled={!ready} aria-label={t('browser.reload')} title={t('browser.reload')} onClick={() => act('browserReload')}>
          <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M10 6a4 4 0 1 1-1.2-2.85M10 1.5v2.5H7.5" /></svg>
        </button>
        <input className="browser-address" data-testid="browser-address" disabled={!ready} value={address} placeholder={t('browser.address.placeholder')} aria-label={t('browser.address.placeholder')} spellCheck={false}
          onFocus={() => { editing.current = true; }} onBlur={() => { editing.current = false; setAddress(state.url ?? ''); }}
          onChange={(event) => setAddress(event.target.value)} onKeyDown={keyDown} />
      </div>
      <div className="browser-slot" ref={slot} data-testid="browser-slot">
        {state.status !== 'ready' && state.status !== 'starting' && (
          <div className="browser-notice" data-testid="browser-notice">
            <svg viewBox="0 0 36 36" aria-hidden="true"><circle cx="18" cy="18" r="15" /><path d="M3 18h30M18 3c-5 5-5 25 0 30M18 3c5 5 5 25 0 30" /></svg>
            <p>{t(state.status === 'disabled' ? 'browser.engine.disabled' : state.status === 'missing' ? 'browser.runtime.missing' : 'browser.engine.failed')}</p>
            {state.status === 'missing' && (
              <>
                <button type="button" disabled={state.installing} onClick={() => act('browserInstall')}>{t('browser.runtime.install')}</button>
                {state.installing && <p>{t('browser.runtime.installing')}</p>}
                {state.installMessage && <p className={state.installFailed ? 'browser-install-failed' : ''}>{state.installMessage}</p>}
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
