import type { RunSession } from '../store/models';
import { useServices } from '../app/context';
import type { Translate } from '../i18n/locale';
import { BrowserView } from './browser/BrowserView';
import { FilesView } from './files/FilesView';
import { TerminalView } from './terminal/TerminalView';

/** The kinds whose pane shows the slim bar and no header of its own (WinUI MainWindow.PaneHeader.cs GroupSlimHeaderKinds). */
export const slimKinds: ReadonlySet<string> = new Set(['browser', 'agent-terminal', 'agent-browser', 'files']);

/** A pane's kind in words, as its slim bar says it (WinUI PaneKindWords, the Mac's DashboardText.kind). */
export function paneKindWords(kind: string, t: Translate): string {
  switch (kind) {
    case 'claude': return t('dashboard.kind.agent');
    case 'shell': return t('dashboard.kind.shell');
    case 'browser': return t('browser.tab.title');
    case 'agent-terminal': return t('dashboard.kind.agentTerminal');
    case 'agent-browser': return t('dashboard.kind.agentBrowser');
    case 'files': return t('files.pane.title');
    default: return kind;
  }
}

/**
 * A pane that is not a conversation (spec §4 slim header; M/PaneChrome.swift:99-141): the slim ink bar with the kind in words on
 * the idle fill, no status and no menu (the pane's name is on its tab), over the pane's own view. A press anywhere in the pane
 * activates it (the layout's pane slot).
 */
export function SlimPane({ session }: { session: RunSession }) {
  const { t } = useServices();
  return (
    <section className="pane pane-slim" data-session-id={session.id} data-pane-kind={session.kind}>
      <header className="pane-slim-header" data-testid="pane-slim-header" role="heading" aria-level={1} aria-label={session.title}>
        <span className="pane-slim-words">{paneKindWords(session.kind, t)}</span>
      </header>
      <div className="pane-slim-body">
        {session.kind === 'files' && <FilesView session={session} />}
        {(session.kind === 'browser' || session.kind === 'agent-browser') && <BrowserView session={session} />}
        {session.kind === 'agent-terminal' && <TerminalView session={session} />}
      </div>
    </section>
  );
}
