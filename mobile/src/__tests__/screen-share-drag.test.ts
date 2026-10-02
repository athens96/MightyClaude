import { createDragTracker } from '@/lib/screen-share/drag';
import type { DragPhase, NormalizedPoint } from '@/lib/screen-share/input';

/** The mouse button goes down only for a real drag, and always comes back up. */

function recorder() {
  const events: [DragPhase, NormalizedPoint][] = [];
  const tracker = createDragTracker((point, phase) => events.push([phase, point]));
  return { events, tracker };
}

describe('a drag on the remote screen', () => {
  it('sends begin, moves and end in that order', () => {
    const { events, tracker } = recorder();
    tracker.begin({ x: 0.1, y: 0.1 });
    tracker.move({ x: 0.2, y: 0.2 });
    tracker.finish({ x: 0.3, y: 0.3 });
    expect(events).toEqual([
      ['begin', { x: 0.1, y: 0.1 }],
      ['move', { x: 0.2, y: 0.2 }],
      ['end', { x: 0.3, y: 0.3 }],
    ]);
  });

  it('never moves or ends a drag that did not begin', () => {
    const { events, tracker } = recorder();
    tracker.move({ x: 0.2, y: 0.2 });
    tracker.finish({ x: 0.3, y: 0.3 });
    expect(events).toEqual([]);
  });

  it('is no drag at all when it starts on a letterbox bar', () => {
    const { events, tracker } = recorder();
    tracker.begin(undefined);
    tracker.move({ x: 0.5, y: 0.5 });
    tracker.finish({ x: 0.5, y: 0.5 });
    expect(events).toEqual([]);
    expect(tracker.active).toBe(false);
  });

  it('still lets the button up when the gesture is cancelled off the picture', () => {
    const { events, tracker } = recorder();
    tracker.begin({ x: 0.4, y: 0.4 });
    tracker.move({ x: 0.6, y: 0.6 });
    tracker.finish(undefined);
    expect(events[events.length - 1]).toEqual(['end', { x: 0.6, y: 0.6 }]);
    expect(tracker.active).toBe(false);
  });

  it('ends exactly once, and takes no second begin while one is open', () => {
    const { events, tracker } = recorder();
    tracker.begin({ x: 0.1, y: 0.1 });
    tracker.begin({ x: 0.9, y: 0.9 });
    tracker.finish({ x: 0.2, y: 0.2 });
    tracker.finish({ x: 0.3, y: 0.3 });
    expect(events.map(([phase]) => phase)).toEqual(['begin', 'end']);
  });
});
