import type { DragPhase, NormalizedPoint } from '@/lib/screen-share/input';

/**
 * One drag as the Mac must see it: `begin` once the pan has really started (a tap never
 * presses the button), `move` only between a `begin` and its `end`, and an `end` after
 * every `begin` — even when the gesture is cancelled — so the Mac never keeps the mouse
 * button down. A drag that starts on a letterbox bar is no drag at all.
 */
export interface DragTracker {
  begin(point: NormalizedPoint | undefined): void;
  move(point: NormalizedPoint | undefined): void;
  /** The gesture is over, however it ended; `point` may be off the picture. */
  finish(point: NormalizedPoint | undefined): void;
  readonly active: boolean;
}

export function createDragTracker(
  send: (point: NormalizedPoint, phase: DragPhase) => void,
): DragTracker {
  let active = false;
  let last: NormalizedPoint | undefined;
  return {
    begin(point) {
      if (active || !point) return;
      active = true;
      last = point;
      send(point, 'begin');
    },
    move(point) {
      if (!active || !point) return;
      last = point;
      send(point, 'move');
    },
    finish(point) {
      if (!active) return;
      active = false;
      const at = point ?? last;
      last = undefined;
      if (at) send(at, 'end');
    },
    get active() {
      return active;
    },
  };
}
