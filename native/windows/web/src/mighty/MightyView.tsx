import { useCallback, useContext, useEffect, useState } from 'react';
import { PaneShownContext } from '../pane/shown';
import { useServices, useStoreState } from '../app/context';
import type { RunSession } from '../store/models';
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

/** The pane header's Default | Mighty switch; the host saves the choice the way the WinUI switch does (SetAgentViewMode). */
export function ModeSwitch({ session }: { session: RunSession }) {
  const { bridge, t } = useServices();
  const mighty = session.agentViewMode === 'mighty';
  return (
    <span className="mighty-switch" role="group" aria-label={t('graph.view.switch')} data-testid="mighty-mode-switch">
      {(['default', 'mighty'] as const).map((mode) => (
        <button key={mode} className={(mode === 'mighty') === mighty ? 'on' : ''} data-mode={mode}
          onClick={() => void bridge.call('mightySetMode', { paneId: session.id, mode })}>
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
  const mighty = useStoreState().mighty[session.id] ?? emptyMighty;
  const presentation = presentationOf(session);
  // A block the diagram opens on next (a timeline row opened, a structure card revealed): selected, the camera on it.
  const [aim, setAim] = useState<Aim | null>(null);
  const aimed = useCallback(() => setAim(null), []);
  const aimAt = useCallback((nodeId: string, fallbackNodeId?: string) => setAim({ nodeId, fallbackNodeId }), []);
  const reveal = useCallback((nodeId: string, fallbackNodeId: string) => {
    aimAt(nodeId, fallbackNodeId);
    void bridge.call('mightySetPresentation', { paneId: session.id, mode: 'diagram' });
  }, [aimAt, bridge, session.id]);
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
              onClick={() => void bridge.call('mightySetPresentation', { paneId: session.id, mode })}>
              <Icon name={mode === 'diagram' ? 'share-2' : mode === 'timeline' ? 'list' : 'columns-3'} size={12} weight={2} />{t('graph.view.' + mode)}
            </button>
          ))}
        </span>
        <span className="mighty-header">{mighty.inputs?.header ?? t('graph.view.mighty')}</span>
        <span className="mighty-summary" title={mighty.inputs?.summaryHelp} data-testid="mighty-summary">{mighty.inputs?.summary ?? ''}</span>
      </div>
      {presentation === 'diagram' ? <Diagram session={session} mighty={mighty} aim={aim} onAimed={aimed} />
        : presentation === 'timeline' ? <Timeline session={session} groups={mighty.timeline} older={mighty.older} onOpenRow={aimAt} />
          : <Structure boards={mighty.structure} onReveal={reveal} />}
    </div>
  );
}
