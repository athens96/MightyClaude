import { useCallback, useMemo, useRef, useState } from 'react';
import type { LayoutChangeEvent, NativeScrollEvent, NativeSyntheticEvent } from 'react-native';
import { Gesture } from 'react-native-gesture-handler';
import {
  PULL_GIVE_UP,
  PULL_START_SLACK,
  followAfterScroll,
  isNearBottom,
  pullPhase,
  type PullPhase,
  type ScrollMetrics,
} from '@/lib/follow';

type ScrollEvent = NativeSyntheticEvent<NativeScrollEvent>;

/** The one thing this needs from a list; every `FlatList` has it. */
export interface EndScrollable {
  scrollToEnd: (params?: { animated?: boolean | null }) => void;
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
  const metrics = useRef<ScrollMetrics>({ contentHeight: 0, viewportHeight: 0, offsetY: 0 });
  const pullStart = useRef<number | null>(null);
  const pullActive = useRef(false);
  const phaseRef = useRef<PullPhase>('idle');
  const [pull, setPull] = useState<PullPhase>('idle');

  const attach = useCallback((instance: EndScrollable | null) => {
    list.current = instance;
    if (instance) following.current = true;
  }, []);

  /** Back to the newest content, e.g. once the user has sent something. */
  const follow = useCallback(() => {
    following.current = true;
    list.current?.scrollToEnd({ animated: false });
  }, []);

  const props = useMemo<FollowBottomProps>(() => {
    const settle = (event: ScrollEvent) => {
      metrics.current = metricsOf(event);
      following.current = followAfterScroll(following.current, metrics.current, true);
    };
    const keepUp = () => {
      if (following.current) list.current?.scrollToEnd({ animated: false });
    };
    return {
      onScroll: (event) => {
        metrics.current = metricsOf(event);
        const byUser = dragging.current || coasting.current;
        following.current = followAfterScroll(following.current, metrics.current, byUser);
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
      scrollEventThrottle: 64,
    };
  }, [onScroll]);

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

  return { attach, follow, props, pull, pullGesture };
}
