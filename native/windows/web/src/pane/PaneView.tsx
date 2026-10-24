import { memo, useCallback, useEffect, useRef, useState } from 'react';
import type { RunSession } from '../store/models';
import { useServices, useStoreState } from '../app/context';
import { Conversation } from '../conversation/Conversation';
import { Composer } from '../composer/Composer';
import { Cards } from '../cards/Cards';
import { clockLabel } from '../composer/clock';
import { MightyView, ModeSwitch, showsModeSwitch } from '../mighty/MightyView';
import { SlimPane, slimKinds } from '../panes/Panes';
import { TerminalView } from '../panes/terminal/TerminalView';
import { ReferenceContext } from '../conversation/referenceClick';
import type { ReferenceTarget } from '../conversation/references';
import { ReferenceBubble, type ReferenceRequest } from './ReferenceBubble';

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
  const store = useStoreState();
  const header = store.composers[session.id]?.header;
  const agent = session.kind === 'claude' || session.kind === 'shell';
  const switchable = showsModeSwitch(session);
  const mighty = switchable && session.agentViewMode === 'mighty';
  // A remote pane's files are on its host: no bubble opens there.
  const remote = !!store.snapshot?.workspaces.find((w) => w.id === session.workspaceId)?.remote;
  const [reference, setReference] = useState<ReferenceRequest | null>(null);
  const clicks = useRef(0);
  const room = useRef<HTMLDivElement>(null);
  const showReference = useCallback((target: ReferenceTarget) => setReference({ ...target, seq: ++clicks.current }), []);
  const closeReference = useCallback(() => setReference(null), []);
  if (slimKinds.has(session.kind)) return <SlimPane session={session} />;
  // Under the Mighty bar while the Mighty view shows.
  const bubbleTop = mighty ? room.current?.querySelector<HTMLElement>('.mighty-bar')?.offsetHeight ?? 0 : 0;
  // A shell's terminal stands where the conversation and the composer are; a remote workspace's shell keeps them (its commands go to the host).
  const body = (
    <>
      <ReferenceContext.Provider value={remote ? null : showReference}>
        <div className="pane-room" ref={room}>
          {mighty ? <MightyView key={'mighty-' + session.id} session={session} /> : <Conversation key={session.id} session={session} />}
          {reference && !remote && <ReferenceBubble paneId={session.id} request={reference} top={bubbleTop} onClose={closeReference} />}
        </div>
      </ReferenceContext.Provider>
      {session.kind === 'claude' && <Cards key={'cards-' + session.id} paneId={session.id} />}
      {agent && <Composer key={'composer-' + session.id} session={session} />}
    </>
  );
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
        {switchable && <ModeSwitch session={session} />}
      </header>
      {session.kind === 'shell' ? <TerminalView key={'terminal-' + session.id} session={session} fallback={body} /> : body}
    </section>
  );
});
