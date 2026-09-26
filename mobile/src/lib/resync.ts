import type { RelayState } from '@/api/relay/transport';

/**
 * When a screen has to ask the host again from scratch. A long poll only hears about
 * changes it is awake for: while the phone sat in the background its tunnel could die
 * or freeze, the host moved on — a run finished — and nothing arrives afterwards to
 * say so. The screen then kept showing 중지 for a run that was long over.
 *
 * Pure: AppState, the relay and the store are wired up in the hooks and screens.
 */

/**
 * True when the app has just come back from the background. `inactive` → `active` is
 * not a return: iOS passes through `inactive` for Control Center, the notification
 * shade, Face ID and system alerts, and restarting every poll on each of those would
 * pile abandoned long polls onto the tunnel's request slots.
 */
export function returnedToForeground(previous: string | null | undefined, next: string): boolean {
  return next === 'active' && previous === 'background';
}

/**
 * True when the tunnel is usable again after being anything else — a reconnect. What
 * was asked on the old socket died with it, and what changed meanwhile was never
 * pushed, so the screen starts over rather than trusting what it holds.
 */
export function relayCameBack(previous: RelayState | undefined, next: RelayState): boolean {
  return next === 'ready' && previous !== undefined && previous !== 'ready';
}

/**
 * Whether a long-poll answer is the host's whole current state rather than the next
 * step after the one we hold. The first answer of a cycle always is. So is one whose
 * revision went backwards: requests in one cycle go out one at a time, so that only
 * happens when the host started counting again (the Mac app restarted, or its mobile
 * remote was switched off and on) — and dropping it would freeze the screen for good.
 */
export function isFreshAnswer(since: number | undefined, revision: number): boolean {
  return since === undefined || revision < since;
}

/** What the phone makes of the answer to 중지. */
export type StopVerdict =
  | { kind: 'requested' }
  /** Nothing was running: the composer goes back to 보내기 before the refresh lands. */
  | { kind: 'notRunning' }
  | { kind: 'failed'; message: string };

/**
 * Reads the host's answer to a stop. `stopped: false` is the host saying there was no
 * run left to stop — the phone had missed the end of it. A host from before that field
 * meant anything answers `true` or leaves it out, which reads as before. A refusal
 * arrives as its message (`describeError`), which the screen shows as it is.
 */
export function stopVerdict(outcome: { stopped?: boolean } | { failure: string }): StopVerdict {
  if ('failure' in outcome) return { kind: 'failed', message: outcome.failure };
  return outcome.stopped === false ? { kind: 'notRunning' } : { kind: 'requested' };
}

/**
 * Whether the composer offers 중지. After the host said nothing was running, the
 * detail it answered from is known to be stale, so it no longer counts as running —
 * until a newer detail arrives and speaks for itself.
 */
export function composerRunning(
  status: string | undefined,
  revision: number | undefined,
  settledRevision: number | undefined,
): boolean {
  if (status !== 'running') return false;
  return settledRevision === undefined || revision !== settledRevision;
}
