// Where a view moves when a request block appears (natural submit B2; pending.json `scrollTargets`, rule `scrollRule`), the
// Mac's SubmitScroll.target and Windows Core's SubmitScroll.Decide. The caller decides `nearBottom` with its own threshold
// (32 px from the bottom here; in 흐름 the follow camera being on; in 기록 and 파이프라인 being at the newest end). Pure.
import { ease } from '../theme/tokens';

/** The four views of an agent pane: the default transcript, 흐름, 기록 and 파이프라인. */
export type SubmitView = 'basic' | 'diagram' | 'timeline' | 'pipeline';

export type SubmitTarget = 'toRequest' | 'follow' | 'none';

export interface SubmitDecision {
  scroll: SubmitTarget;
  /** The 파이프라인 goes back to the newest board first. */
  resetBoard: boolean;
}

/**
 * The view a pane shows: a pane outside the Mighty view is the transcript; in it, a saved `graphViewMode` that is not the
 * timeline or the structure is the diagram, as MightyTimeline.Mode reads it.
 */
export function submitViewFor(showsMighty: boolean, graphViewMode: string | undefined): SubmitView {
  if (!showsMighty) return 'basic';
  return graphViewMode === 'timeline' ? 'timeline' : graphViewMode === 'structure' ? 'pipeline' : 'diagram';
}

/**
 * `own`: this composer's Enter, a queued request that now runs, 바로 실행 or a selected Ctrl+Enter child. An own request always
 * moves to its block, back to the newest 파이프라인 board when an older one is chosen. Anything else follows only near the
 * bottom, and never on an older 파이프라인 board, which the user chose (its badge says there is more).
 */
export function submitScroll(view: SubmitView, own: boolean, chosenBoardIsNewest: boolean, nearBottom: boolean): SubmitDecision {
  const olderBoard = view === 'pipeline' && !chosenBoardIsNewest;
  if (own) return { scroll: 'toRequest', resetBoard: olderBoard };
  if (olderBoard) return { scroll: 'none', resetBoard: false };
  return { scroll: nearBottom ? 'follow' : 'none', resetBoard: false };
}

/** A scroller this close to its bottom follows it (the conversation's threshold, src/conversation/virtual.ts). */
export const NearBottom = 32;

export const isNearBottom = (scrollHeight: number, scrollTop: number, clientHeight: number, threshold = NearBottom) =>
  scrollHeight - scrollTop - clientHeight <= threshold;

/** The position `progress` (0–1 of the time) of the way from `from` to `to` along the standard easing. */
export const eased = (from: number, to: number, progress: number) => from + (to - from) * ease(progress);

/**
 * Moves a scroller's top to `target()` over `duration` ms along the standard easing (Motion.scroll), or at once when
 * `instant` (Reduce Motion: no movement, the same place). `target` is read each frame, so a block that grows while the view
 * moves is still reached. Returns a cancel; the user's own wheel or drag should call it.
 */
export function animateScrollTop(element: HTMLElement, target: () => number, duration: number, instant: boolean): () => void {
  if (instant || duration <= 0 || typeof requestAnimationFrame !== 'function') {
    element.scrollTop = target();
    return () => {};
  }
  const from = element.scrollTop;
  const start = performance.now();
  let frame = 0;
  const step = () => {
    const progress = Math.min(1, (performance.now() - start) / duration);
    element.scrollTop = eased(from, target(), progress);
    if (progress < 1) frame = requestAnimationFrame(step);
  };
  frame = requestAnimationFrame(step);
  return () => cancelAnimationFrame(frame);
}
