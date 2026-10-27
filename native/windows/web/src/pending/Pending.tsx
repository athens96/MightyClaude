import { useServices } from '../app/context';
import { Icon } from '../icons/Icon';
import type { PendingState } from './display';
import { useBusyState, usePending } from './usePending';

/** The activity bars a pending action shows (the diagram's MightyGraphActivityIndicator); still under Reduce Motion. */
export function PendingBars() {
  return <span className="pending-bars" aria-hidden="true">{[0, 1, 2, 3].map((i) => <span key={i} style={{ animationDelay: `${-(i / 5) * 1.6}s` }} />)}</span>;
}

type PendingProps = {
  /** The words beside the bars; 전송 중 (graph.state.sending) by default. */
  label?: string;
  /** The words in a pill beside the bars; false draws the bars alone (a control's own place, a busy row with its own words). */
  showsLabel?: boolean;
  /** A failure's message in place; false when the caller shows it itself (a card's error line). */
  showsFailure?: boolean;
  className?: string;
} & ({ state: PendingState; busy?: undefined } | { busy: boolean; state?: undefined });

/**
 * One pending action's place (natural submit A3, the Mac's PendingIndicatorView): nothing until the pending rule shows it,
 * then the bars and the label, and a failure's message at once in place. Every button, card and row that waits on the host
 * shows its wait with it: `state` for an action the page tracks, `busy` for a flag the host keeps.
 */
export function Pending(props: PendingProps) {
  return props.state ? <PendingView {...props} state={props.state} /> : <BusyPending {...props} busy={props.busy ?? false} />;
}

function BusyPending(props: Omit<PendingProps, 'state' | 'busy'> & { busy: boolean }) {
  const state = useBusyState(props.busy);
  return <PendingView {...props} state={state} />;
}

function PendingView({ state, label, showsLabel = true, showsFailure = true, className }: Omit<PendingProps, 'state' | 'busy'> & { state: PendingState }) {
  const { t } = useServices();
  const shown = usePending(state);
  const words = label ?? t('graph.state.sending');
  if (shown.kind === 'hidden') return null;
  if (shown.kind === 'failed') {
    if (!showsFailure) return null;
    return (
      <span className={'pending-failed' + (className ? ' ' + className : '')} role="alert" data-pending="failed">
        <Icon name="circle-x" size={11} weight={2} />{shown.message}
      </span>
    );
  }
  return (
    <span className={'pending' + (className ? ' ' + className : '')} role="status" aria-label={words} data-pending="loading">
      <PendingBars />
      {showsLabel && <span className="pending-pill">{words}</span>}
    </span>
  );
}
