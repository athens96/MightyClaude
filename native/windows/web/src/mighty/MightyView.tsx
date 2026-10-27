import { useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { PaneShownContext } from '../pane/shown';
import { useServices, useStoreState } from '../app/context';
import type { RunSession } from '../store/models';
import type { ViewChoice } from '../store/store';
import { shownInMighty } from '../pending/requests';
import { nodeId, RequestStep } from './layout';
import { Diagram, type Aim } from './Diagram';
import { Structure } from './Structure';
import { Timeline } from './Timeline';
import { emptyMighty } from './types';
import { Icon } from '../icons/Icon';

/** Panes that offer the Default | Mighty switch (MightyGraphViewModel.ShowsModeSwitch). */
export const showsModeSwitch = (session: RunSession) => session.kind === 'claude' && ['claude', 'codex', 'gemini'].includes(session.provider);

/** The view the pane is saved in (MightyTimeline.Mode): an unknown word is the diagram. */
export const presentationOf = (session: RunSession) =>
  session.graphViewMode === 'timeline' || session.graphViewMode === 'structure' ? session.graphViewMode : 'diagram';

/**
 * The pane as the page shows it: a view switch clicked a moment ago shows at once, before the host has saved it (the store's
 * `views`, dropped once the host's state holds it). The same object while nothing was clicked.
 */
export function useShownSession(session: RunSession): RunSession {
  const choice = useStoreState().views[session.id];
  return useMemo(() => {
    if (!choice) return session;
    return { ...session, ...(choice.mode !== undefined ? { agentViewMode: choice.mode } : {}), ...(choice.presentation !== undefined ? { graphViewMode: choice.presentation } : {}) };
  }, [session, choice]);
}

/** A view switch: drawn at once, saved by the host (SetAgentViewMode, the Mighty view's mode), taken back if the call fails. */
export function useViewSwitch(paneId: string): (choice: ViewChoice) => void {
  const { bridge, store } = useServices();
  return useCallback((choice: ViewChoice) => {
    store.dispatch({ kind: 'view', paneId, choice });
    const saved = choice.mode !== undefined ? bridge.call('mightySetMode', { paneId, mode: choice.mode }) : bridge.call('mightySetPresentation', { paneId, mode: choice.presentation });
    saved.catch(() => store.dispatch({ kind: 'view', paneId, choice, revert: true }));
  }, [bridge, store, paneId]);
}

/** The pane header's Default | Mighty switch; the host saves the choice the way the WinUI switch does (SetAgentViewMode). */
export function ModeSwitch({ session }: { session: RunSession }) {
  const { t } = useServices();
  const choose = useViewSwitch(session.id);
  const mighty = session.agentViewMode === 'mighty';
  return (
    <span className="mighty-switch" role="group" aria-label={t('graph.view.switch')} data-testid="mighty-mode-switch">
      {(['default', 'mighty'] as const).map((mode) => (
        <button key={mode} className={(mode === 'mighty') === mighty ? 'on' : ''} data-mode={mode}
          onClick={() => { if ((mode === 'mighty') !== mighty) choose({ mode }); }}>
          <Icon name={mode === 'mighty' ? 'share-2' : 'text-align-start'} size={12} weight={2} />{t('graph.view.' + mode)}
        </button>
      ))}
    </span>
  );
}

/**
 * The pane's Mighty view in place of its conversation: the bar (diagram | timeline | structure, the style title, the pane's
 * totals) over the chosen view. While it is on the page the host pushes its model (`mightyVisible`).
 */
export function MightyView({ session }: { session: RunSession }) {
  const { bridge, t } = useServices();
  const stored = useStoreState();
  const mighty = stored.mighty[session.id] ?? emptyMighty;
  const presentation = presentationOf(session);
  const choose = useViewSwitch(session.id);
  // The request the composer just sent, drawn in every view until the host's block replaces it (natural submit B1), and the
  // count of own sends every view moves on (B2).
  const pending = shownInMighty(stored.pending[session.id], session);
  const own = stored.ownSubmits[session.id] ?? 0;
  // A block the diagram opens on next (a timeline row opened, a structure card revealed): selected, the camera on it.
  const [aim, setAim] = useState<Aim | null>(null);
  const aimed = useCallback(() => setAim(null), []);
  const aimAt = useCallback((nodeId: string, fallbackNodeId?: string) => setAim({ nodeId, fallbackNodeId }), []);
  const reveal = useCallback((nodeId: string, fallbackNodeId: string) => {
    aimAt(nodeId, fallbackNodeId);
    if (presentationOf(session) !== 'diagram') choose({ presentation: 'diagram' });
  }, [aimAt, choose, session]);
  // A send that went into the running run as a new turn (steered): the diagram opens on its steer block, else on the request.
  const steered = stored.steered;
  const steerSeen = useRef(steered?.seq ?? 0);
  useEffect(() => {
    if (!steered || steered.paneId !== session.id || steered.seq === steerSeen.current) return;
    steerSeen.current = steered.seq;
    const runs = session.graphRuns ?? [];
    const run = runs.find((r) => r.id === steered.runId) ?? runs[runs.length - 1];
    if (!run) return;
    const steer = [...run.agents].reverse().find((agent) => agent.kind === 'steer');
    aimAt(steer ? nodeId(run.id, 'agent:' + steer.id) : nodeId(run.id, RequestStep), nodeId(run.id, RequestStep));
  }, [steered, session.id, session.graphRuns, aimAt]);
  // A pane kept mounted behind another tab is not on the page: the host pushes its model only while it shows.
  const shown = useContext(PaneShownContext);
  useEffect(() => {
    if (!shown) return;
    void bridge.call('mightyVisible', { paneId: session.id, visible: true });
    return () => void bridge.call('mightyVisible', { paneId: session.id, visible: false });
  }, [bridge, session.id, shown]);
  return (
    <div className="mighty" data-testid="mighty" data-presentation={presentation}>
      <div className="mighty-bar">
        <span className="mighty-switch" role="group" aria-label={t('graph.view.switch')}>
          {(['diagram', 'timeline', 'structure'] as const).map((mode) => (
            <button key={mode} className={mode === presentation ? 'on' : ''} data-presentation={mode}
              onClick={() => { if (mode !== presentation) choose({ presentation: mode }); }}>
              <Icon name={mode === 'diagram' ? 'share-2' : mode === 'timeline' ? 'list' : 'columns-3'} size={12} weight={2} />{t('graph.view.' + mode)}
            </button>
          ))}
        </span>
        <span className="mighty-header">{mighty.inputs?.header ?? t('graph.view.mighty')}</span>
        <span className="mighty-summary" title={mighty.inputs?.summaryHelp} data-testid="mighty-summary">{mighty.inputs?.summary ?? ''}</span>
      </div>
      {presentation === 'diagram' ? <Diagram session={session} mighty={mighty} aim={aim} onAimed={aimed} pending={pending} own={own} />
        : presentation === 'timeline' ? <Timeline session={session} groups={mighty.timeline} older={mighty.older} history={mighty.inputs?.history} onOpenRow={aimAt} pending={pending} own={own} />
          : <Structure boards={mighty.structure} onReveal={reveal} pending={pending} own={own} />}
    </div>
  );
}
