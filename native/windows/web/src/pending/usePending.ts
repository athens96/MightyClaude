// The page's hooks over the pending rule (display.ts): what a pending state shows now, redrawn only at the moments it may
// change; a busy flag turned into a pending state; and an action whose pending state follows its promise.
import { useCallback, useEffect, useRef, useState } from 'react';
import { idle, nextPendingChange, pendingIndicator, type PendingIndicator, type PendingState } from './display';

/** What `state` shows now (natural submit A1), drawn again only when the rule says it may change, never frame by frame. */
export function usePending(state: PendingState): PendingIndicator {
  const [now, setNow] = useState(() => Date.now());
  const current = Date.now();
  useEffect(() => {
    const next = nextPendingChange(state, Date.now());
    if (next === null) return undefined;
    const timer = setTimeout(() => setNow(Date.now()), Math.max(0, next - Date.now()));
    return () => clearTimeout(timer);
  }, [state, now]);
  // The state may be newer than the last timer: it is read at the draw's own time.
  return pendingIndicator(state, Math.max(now, current));
}

/**
 * A busy flag as a pending state: pending from the moment it turned on, done when it turned off. A flag the host keeps (a card
 * that is sending, a clone that checks) shows its loading by the same rule as everything else.
 */
export function useBusyState(busy: boolean): PendingState {
  const [state, setState] = useState<PendingState>(() => (busy ? { kind: 'pending', since: Date.now() } : idle));
  const last = useRef(busy);
  useEffect(() => {
    if (last.current === busy) return;
    last.current = busy;
    setState((previous) => {
      if (busy) return { kind: 'pending', since: Date.now() };
      return previous.kind === 'pending' ? { kind: 'done', since: previous.since, at: Date.now() } : idle;
    });
  }, [busy]);
  return state;
}

/** What an action's work answers: done, or failed with the message its place shows. */
export type ActionResult = { ok: true } | { ok: false; message: string };

/**
 * An action's pending state, following the promise `run` is given: pending while it runs, then done or failed. `running` is
 * true while it runs, so its controls lock (a second press does nothing). A thrown error is a failure with its message.
 */
export function usePendingAction(): { state: PendingState; running: boolean; run: (work: () => Promise<ActionResult>) => Promise<boolean> } {
  const [state, setState] = useState<PendingState>(idle);
  const busy = useRef(false);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);
  const run = useCallback(async (work: () => Promise<ActionResult>) => {
    if (busy.current) return false;
    busy.current = true;
    const since = Date.now();
    setState({ kind: 'pending', since });
    let result: ActionResult;
    try {
      result = await work();
    } catch (failure) {
      result = { ok: false, message: failure instanceof Error ? failure.message : String(failure) };
    }
    busy.current = false;
    if (mounted.current) setState(result.ok ? { kind: 'done', since, at: Date.now() } : { kind: 'failed', message: result.message, since, at: Date.now() });
    return result.ok;
  }, []);
  return { state, running: state.kind === 'pending', run };
}
