import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { LayoutChangeEvent, NativeScrollEvent, NativeSyntheticEvent } from 'react-native';
import { Gesture } from 'react-native-gesture-handler';
import {
  PULL_GIVE_UP,
  PULL_START_SLACK,
  SETTLE_FRAMES,
  bottomOffset,
  followAfterScroll,
  isMeasured,
  isNearBottom,
  pullPhase,
  type PullPhase,
  type ScrollMetrics,
} from '@/lib/follow';

type ScrollEvent = NativeSyntheticEvent<NativeScrollEvent>;

/** The two things this needs from a list; every `FlatList` has them. */
export interface EndScrollable {
  scrollToEnd: (params?: { animated?: boolean | null }) => void;
  scrollToOffset: (params: { offset: number; animated?: boolean | null }) => void;
}

/** Spread onto the list that should follow its newest content. */
export interface FollowBottomProps {
  onScroll: (event: ScrollEvent) => void;
  onScrollBeginDrag: () => void;
  onScrollEndDrag: (event: ScrollEvent) => void;
  onMomentumScrollBegin: () => void;
  onMomentumScrollEnd: (event: ScrollEvent) => void;
  onContentSizeChange: (width: number, height: number) => void;
  /** The keyboard shrinking the list is growth too, seen from the other side. */
  onLayout: (event: LayoutChangeEvent) => void;
  /** A finger resting on the list before it moves enough to count as a drag. */
  onTouchStart: () => void;
  onTouchEnd: () => void;
  onTouchCancel: () => void;
  scrollEventThrottle: number;
}

function metricsOf(event: ScrollEvent): ScrollMetrics {
  const { contentOffset, contentSize, layoutMeasurement } = event.nativeEvent;
  return {
    contentHeight: contentSize.height,
    viewportHeight: layoutMeasurement.height,
    offsetY: contentOffset.y,
  };
}

/**
 * Keeps a growing list at its newest content while the user is there, and leaves them
 * alone once they scroll up to read (`lib/follow.ts` decides). The jump to the end is not
 * animated: an animation would send scroll events of its own half-way up the list. A
 * freshly mounted list starts out following, so switching views lands on the newest.
 *
 * The jump goes to the real bottom — the content height and viewport height the list
 * itself reported — rather than through `scrollToEnd`, which adds up estimated row
 * heights and stops short of rows and a footer not measured yet. It is repeated for a
 * couple of frames so it lands where the layout settles, not where it stood at first.
 *
 * `detached` is true while the user reads further up. A list that keeps its visible rows in
 * place (`maintainVisibleContentPosition`) holds them only then: rows trimmed or put in
 * above would otherwise slide under the reader, and a list that is following jumps to its
 * newest content anyway, which the hold would fight.
 *
 * With `onPull`, dragging further up from the very bottom refreshes. A gesture carries
 * it rather than scroll events or raw touches: Android reports no offset past the end,
 * and its scroll view cancels a view's own touches once it starts dragging. The gesture
 * waits in manual activation, gives up at once unless the finger started at the bottom
 * and moves up, and so never takes an ordinary scroll away from the list.
 */
export function useFollowBottom(onScroll?: (event: ScrollEvent) => void, onPull?: () => void) {
  const list = useRef<EndScrollable | null>(null);
  const following = useRef(true);
  const dragging = useRef(false);
  const coasting = useRef(false);
  const touching = useRef(false);
  const metrics = useRef<ScrollMetrics>({ contentHeight: 0, viewportHeight: 0, offsetY: 0 });
  const pullStart = useRef<number | null>(null);
  const pullActive = useRef(false);
  const phaseRef = useRef<PullPhase>('idle');
  const [pull, setPull] = useState<PullPhase>('idle');
  const settleFrame = useRef<number | null>(null);
  const settleLeft = useRef(0);
  /**
   * `following`, turned around and made visible to render: true while the user reads
   * further up. It changes only when following flips, not on every scroll event.
   */
  const [detached, setDetached] = useState(false);

  const setFollowing = useCallback((next: boolean) => {
    if (following.current === next) return;
    following.current = next;
    setDetached(!next);
  }, []);

  const attach = useCallback((instance: EndScrollable | null) => {
    list.current = instance;
    if (!instance) return;
    setFollowing(true);
    // A remounted list (switching views) reports its own sizes again; until it has, the
    // last list's would send the jump to the wrong place.
    metrics.current = { contentHeight: 0, viewportHeight: 0, offsetY: 0 };
  }, [setFollowing]);

  /** A finger on the list, or a flick still coasting: the user is reading. */
  const reading = useCallback(
    () => touching.current || dragging.current || coasting.current || pullStart.current !== null,
    [],
  );

  const jump = useCallback(() => {
    const target = list.current;
    if (!target) return;
    if (isMeasured(metrics.current)) target.scrollToOffset({ offset: bottomOffset(metrics.current), animated: false });
    else target.scrollToEnd({ animated: false });
  }, []);

  /** Jumps to the newest content now, then again on the next frames as the layout settles. */
  const pin = useCallback(() => {
    jump();
    settleLeft.current = SETTLE_FRAMES;
    if (settleFrame.current !== null) return;
    const settle = () => {
      settleFrame.current = null;
      if (!following.current || reading()) return;
      jump();
      settleLeft.current -= 1;
      if (settleLeft.current > 0) settleFrame.current = requestAnimationFrame(settle);
    };
    settleFrame.current = requestAnimationFrame(settle);
  }, [jump, reading]);

  useEffect(
    () => () => {
      if (settleFrame.current !== null) cancelAnimationFrame(settleFrame.current);
    },
    [],
  );

  /** Back to the newest content, e.g. once the user has sent something. */
  const follow = useCallback(() => {
    setFollowing(true);
    pin();
  }, [pin, setFollowing]);

  /**
   * Back to the newest content because the work moved on, unless the user's finger is on
   * the list or it is still coasting from a flick: then they are reading, and are left be.
   */
  const followProgress = useCallback(() => {
    if (reading()) return;
    setFollowing(true);
    pin();
  }, [pin, reading, setFollowing]);

  const props = useMemo<FollowBottomProps>(() => {
    const settle = (event: ScrollEvent) => {
      metrics.current = metricsOf(event);
      setFollowing(followAfterScroll(following.current, metrics.current, true));
    };
    const keepUp = () => {
      if (following.current && !dragging.current && !coasting.current) pin();
    };
    return {
      onScroll: (event) => {
        metrics.current = metricsOf(event);
        const byUser = dragging.current || coasting.current;
        setFollowing(followAfterScroll(following.current, metrics.current, byUser));
        onScroll?.(event);
      },
      onScrollBeginDrag: () => {
        dragging.current = true;
      },
      onScrollEndDrag: (event) => {
        dragging.current = false;
        settle(event);
      },
      onMomentumScrollBegin: () => {
        coasting.current = true;
      },
      onMomentumScrollEnd: (event) => {
        coasting.current = false;
        settle(event);
      },
      onContentSizeChange: (_width, height) => {
        metrics.current = { ...metrics.current, contentHeight: height };
        keepUp();
      },
      onLayout: (event) => {
        metrics.current = { ...metrics.current, viewportHeight: event.nativeEvent.layout.height };
        keepUp();
      },
      // Android cancels these once its scroll view takes the drag; `dragging` covers from there.
      onTouchStart: () => {
        touching.current = true;
      },
      onTouchEnd: () => {
        touching.current = false;
      },
      onTouchCancel: () => {
        touching.current = false;
      },
      scrollEventThrottle: 64,
    };
  }, [onScroll, pin, setFollowing]);

  const pullGesture = useMemo(() => {
    const toPhase = (next: PullPhase) => {
      if (phaseRef.current === next) return;
      phaseRef.current = next;
      setPull(next);
    };
    const lifted = (y: number) => (pullStart.current === null ? 0 : pullStart.current - y);
    return Gesture.Pan()
      .enabled(onPull !== undefined)
      .runOnJS(true)
      .manualActivation(true)
      .onTouchesDown((event, state) => {
        // A second finger joining a pull must not move where it started.
        if (event.numberOfTouches > 1) return;
        toPhase('idle');
        const measured = metrics.current.viewportHeight > 0;
        const touch = event.allTouches[0];
        pullStart.current = measured && touch && isNearBottom(metrics.current, PULL_START_SLACK) ? touch.absoluteY : null;
        if (pullStart.current === null) state.fail();
      })
      .onTouchesMove((event, state) => {
        const touch = event.allTouches[0];
        if (!touch || pullStart.current === null) return;
        const by = lifted(touch.absoluteY);
        // Once active a gesture cannot fail any more, so a pull the finger turns back on
        // just rides out to release, where it only refreshes if still armed.
        if (!pullActive.current && by < -PULL_GIVE_UP) state.fail();
        else if (!pullActive.current && by > PULL_GIVE_UP) {
          pullActive.current = true;
          state.activate();
        }
        toPhase(pullPhase(by));
      })
      .onEnd(() => {
        if (phaseRef.current === 'armed') onPull?.();
      })
      .onFinalize(() => {
        pullStart.current = null;
        pullActive.current = false;
        toPhase('idle');
      });
  }, [onPull]);

  return { attach, detached, follow, followProgress, props, pull, pullGesture };
}
