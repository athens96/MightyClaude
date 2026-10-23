import { memo, useEffect, useState } from 'react';
import type { RunSession } from '../store/models';
import { useServices, useStoreState } from '../app/context';
import { Conversation } from '../conversation/Conversation';
import { Composer } from '../composer/Composer';
import { clockLabel } from '../composer/clock';

/** The header's clock, redrawn each second while the run is not finished (MW.Composer.cs RefreshElapsed). */
function Clock({ session }: { session: RunSession }) {
  const { t } = useServices();
  const [now, setNow] = useState(() => Date.now());
  const timing = session.runTiming;
  const running = timing !== undefined && !timing.finishedAt;
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);
  if (!timing) return null;
  return <span className="pane-clock" data-testid="pane-clock">{clockLabel(timing, now, t('dashboard.card.approximate'))}</span>;
}

/**
 * The selected pane: its header (title, state word and figures, spec section 1) over its conversation and, for an agent or a
 * shell, the composer. The word and the figures after the clock are the host's (MainWindow.WebShell.Composer.cs WebHeader).
 */
export const PaneView = memo(function PaneView({ session }: { session: RunSession }) {
  const { t } = useServices();
  const header = useStoreState().composers[session.id]?.header;
  const agent = session.kind === 'claude' || session.kind === 'shell';
  return (
    <section className="pane" data-session-id={session.id}>
      <header className="pane-header">
        <h1 className="pane-title" data-testid="pane-title">{session.title}</h1>
        <span className={'pane-status tone-' + (header?.tone ?? 'idle')} data-testid="pane-state">{header?.word ?? t('session.state.' + session.status)}</span>
        {header && (
          <span className="pane-figures" data-testid="pane-figures" title={header.help}>
            {header.timed && <Clock session={session} />}
            {header.rest}
          </span>
        )}
      </header>
      <Conversation key={session.id} session={session} />
      {agent && <Composer key={'composer-' + session.id} session={session} />}
    </section>
  );
});
