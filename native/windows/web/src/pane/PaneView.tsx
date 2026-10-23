import { memo } from 'react';
import type { RunSession } from '../store/models';
import { useServices } from '../app/context';

/** How many of the newest entries the phase-0 placeholder shows. */
export const shownEntries = 50;

/** The selected pane: its title and its newest entries as plain text (phase 1 replaces this with the conversation). */
export const PaneView = memo(function PaneView({ session }: { session: RunSession }) {
  const { t } = useServices();
  const entries = session.logs.slice(-shownEntries);
  return (
    <section className="pane" data-session-id={session.id}>
      <header className="pane-header">
        <h1 className="pane-title" data-testid="pane-title">{session.title}</h1>
        <span className={'pane-status status-' + session.status}>{t('session.state.' + session.status)}</span>
      </header>
      <ol className="pane-log">
        {entries.map((entry) => (
          <li key={entry.id} className={'pane-entry kind-' + entry.kind}>
            <pre>{entry.text}</pre>
          </li>
        ))}
      </ol>
    </section>
  );
});
