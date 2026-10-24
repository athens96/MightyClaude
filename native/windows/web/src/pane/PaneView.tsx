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
import { Icon } from '../icons/Icon';
import { useCall, useMenu, type MenuItem } from '../shell/parts';

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

/** The pane's ··· menu (M/SessionPaneView.swift paneMenu): rename, focus or back to the layout, a new conversation, close. */
function PaneMenu({ session }: { session: RunSession }) {
  const { bridge, t } = useServices();
  const menu = useMenu();
  const call = useCall();
  const focused = useStoreState().shell?.layout?.mode === 'focus';
  const items: MenuItem[] = [
    { label: t('menu.rename'), action: () => call('renameSession', { id: session.id }) },
    { label: t(focused ? 'pane.menu.restoreLayout' : 'menu.focusPane'), action: () => call('focusPane', { id: session.id, toggle: true }) },
  ];
  if (session.kind !== 'shell') items.push({ label: t('pane.menu.newConversation'), disabled: session.status === 'running' || !session.resumeId, action: () => void bridge.call('newConversation', { paneId: session.id }).catch(() => {}) });
  items.push({ separator: true }, { label: t('menu.closePane'), action: () => call('closeSession', { id: session.id }) });
  return (
    <button type="button" className="icon-button pane-menu-button" title={t('pane.menu.accessibility')} aria-label={t('pane.menu.accessibility')} data-testid="pane-menu"
      onClick={(event) => menu.under(event.currentTarget, items)}><Icon name="ellipsis" size={15} weight={2.6} /></button>
  );
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
  const word = header?.word ?? t('session.state.' + session.status);
  // A shell keeps the slim ink bar (M/PaneChrome.swift SlimPaneHeader): what it is, its state in a pill, its menu.
  if (session.kind === 'shell') return (
    <section className="pane pane-shell" data-session-id={session.id}>
      <header className="pane-slim-header" data-testid="pane-slim-header">
        <h1 className="visually-hidden" data-testid="pane-title">{session.title}</h1>
        <span className="pane-slim-words">{t('dashboard.kind.shell') + (remote ? '' : ' · ' + t('phone.card.localTerminal'))}</span>
        <span className="spacer" />
        <span className="hero-status-pill" data-testid="pane-state">{word}</span>
        <PaneMenu session={session} />
      </header>
      <TerminalView key={'terminal-' + session.id} session={session} fallback={body} />
    </section>
  );
  // An agent's header (M/SessionPaneView.swift agentHeader): its state word in its ink, the figures in mono fading at the right,
  // then the Default | Mighty switch and the pane's menu. The name is on the tab right above, so it is not said twice.
  return (
    <section className="pane" data-session-id={session.id}>
      <header className="pane-header">
        <h1 className="visually-hidden" data-testid="pane-title">{session.title}</h1>
        <span className={'pane-status tone-' + (header?.tone ?? 'idle')} data-testid="pane-state">{word}</span>
        <span className="pane-figures" data-testid="pane-figures" title={header?.help}>
          {header?.timed && <Clock session={session} />}
          {header?.rest}
        </span>
        <span className="pane-header-controls">
          {switchable && <ModeSwitch session={session} />}
          <PaneMenu session={session} />
        </span>
      </header>
      {body}
    </section>
  );
});
