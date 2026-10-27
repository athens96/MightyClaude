// Where a view moves when a request block appears (natural submit B2; pending.json `scrollTargets`, rule `scrollRule`): the
// Mac's SubmitScroll.target, Windows Core's SubmitScroll.Decide and the Windows page's submitScroll. The caller decides
// `nearBottom` with its own threshold (here `lib/follow.ts` FOLLOW_THRESHOLD; in 파이프라인 being at the newest end). Pure.

/** The views of an agent pane: the transcript, 흐름 (the phone's block list) and 파이프라인; the phone has no 기록. */
export type SubmitView = 'basic' | 'diagram' | 'timeline' | 'pipeline';

export type SubmitTarget = 'toRequest' | 'follow' | 'none';

export interface SubmitDecision {
  scroll: SubmitTarget;
  /** The 파이프라인 goes back to the newest board first. */
  resetBoard: boolean;
}

/** The session screen's body (`log`, `blocks`, `structure`) as the shared rule names it. */
export function submitViewFor(body: 'log' | 'blocks' | 'structure'): SubmitView {
  return body === 'blocks' ? 'diagram' : body === 'structure' ? 'pipeline' : 'basic';
}

/**
 * `own`: this composer's send, a queued request that now runs, or 다음 실행. An own request always moves to its block, back to
 * the newest 파이프라인 board when an older one is chosen. Anything else follows only near the bottom, and never on an older
 * 파이프라인 board, which the user chose (its badge says there is more).
 */
export function submitScroll(view: SubmitView, own: boolean, chosenBoardIsNewest: boolean, nearBottom: boolean): SubmitDecision {
  const olderBoard = view === 'pipeline' && !chosenBoardIsNewest;
  if (own) return { scroll: 'toRequest', resetBoard: olderBoard };
  if (olderBoard) return { scroll: 'none', resetBoard: false };
  return { scroll: nearBottom ? 'follow' : 'none', resetBoard: false };
}
