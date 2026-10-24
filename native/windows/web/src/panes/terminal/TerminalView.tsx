import { useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import type { RunSession } from '../../store/models';
import { useServices } from '../../app/context';
import { PaneShownContext } from '../../pane/shown';
import { MiddlePath } from '../../shell/parts';
import { fitShown, terminalEntry } from './entries';
import type { TerminalFooter } from './types';

/**
 * A terminal pane (M/LocalTerminalView.swift:67-89, M/AgentTerminalPaneView.swift:57-73; WinUI MainWindow.Terminal.cs): the
 * terminal edge to edge, under it the notice with the shell's restart button while there is one, and the footer line (engine
 * and title, working directory cut in its middle, the grid size). The shell's lifecycle stays the host's (PseudoTerminal);
 * a pane with no terminal here (a remote workspace's shell) shows `fallback` instead.
 */
export function TerminalView({ session, fallback }: { session: RunSession; fallback?: ReactNode }) {
  const { bridge, store, t } = useServices();
  const shown = useContext(PaneShownContext);
  const host = useRef<HTMLDivElement>(null);
  const paneId = session.id;
  const [footer, setFooter] = useState<TerminalFooter | undefined>(() => terminalEntry(paneId)?.footer);

  useEffect(() => {
    let live = true;
    void bridge.call<TerminalFooter>('terminalOpen', { paneId }).then((value) => {
      if (!live || !value) return;
      setFooter(value);
      if (!value.available) return;
      // xterm loads with the first terminal shown (its own chunk); the host appears with the first answer, so mount after both.
      void import('./registry').then(({ ensureTerminal, mountTerminal }) => requestAnimationFrame(() => {
        if (!live || !host.current) return;
        const entry = ensureTerminal(paneId, bridge);
        entry.footerListeners.add(setFooter);
        mountTerminal(entry, host.current);
      }));
    }, () => {
      // A host that cannot give this pane a terminal: the pane shows what it would without one.
      if (live) setFooter({ available: false, shell: true, restart: false, restartEnabled: false, engine: '', title: '', directory: '', grid: '' });
    });
    return () => {
      live = false;
      terminalEntry(paneId)?.footerListeners.delete(setFooter);
      // The pane closed: its terminal goes with it. Hidden or in another workspace: it stays, with its buffer.
      setTimeout(() => {
        if (!store.getState().snapshot?.sessions.some((pane) => pane.id === paneId)) terminalEntry(paneId)?.dispose();
      }, 0);
    };
  }, [bridge, store, paneId]);

  const available = footer?.available ?? false;
  useEffect(() => {
    const element = host.current;
    if (!element || !available) return;
    const observer = new ResizeObserver(() => {
      const entry = terminalEntry(paneId);
      if (entry) fitShown(entry);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [paneId, available]);

  useEffect(() => {
    const entry = terminalEntry(paneId);
    if (shown && entry) fitShown(entry);
  }, [shown, paneId]);

  if (footer && !footer.available) return <>{fallback ?? null}</>;
  return (
    <div className="terminal-pane" data-testid="terminal-pane" data-pane-terminal={paneId}>
      <div className="terminal-host" ref={host} aria-label={t('terminal.accessibility')} />
      {footer?.notice && (
        <div className="terminal-notice" data-testid="terminal-notice">
          <span className="terminal-notice-text">{footer.notice}</span>
          {footer.restart && (
            <button type="button" className="terminal-restart" disabled={!footer.restartEnabled} onClick={() => void bridge.call('terminalRestart', { paneId }).catch(() => {})}>
              {t('terminal.restart')}
            </button>
          )}
        </div>
      )}
      <div className="terminal-footer" data-testid="terminal-footer">
        <span className="terminal-footer-name">
          <span className="terminal-footer-engine">{footer?.engine ?? ''}</span>
          {footer?.shell && footer.title && (
            <>
              <span>·</span>
              <span className="terminal-footer-title">{footer.title}</span>
            </>
          )}
        </span>
        <MiddlePath className="terminal-footer-directory" path={footer?.directory ?? ''} />
        <span className="terminal-footer-grid">{footer?.grid ?? ''}</span>
      </div>
    </div>
  );
}
