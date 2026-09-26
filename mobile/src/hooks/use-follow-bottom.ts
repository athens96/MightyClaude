import { useCallback, useMemo, useRef } from 'react';
import type { NativeScrollEvent, NativeSyntheticEvent } from 'react-native';
import { followAfterScroll, type ScrollMetrics } from '@/lib/follow';

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
  onContentSizeChange: () => void;
  /** The keyboard shrinking the list is growth too, seen from the other side. */
  onLayout: () => void;
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
 */
export function useFollowBottom(onScroll?: (event: ScrollEvent) => void) {
  const list = useRef<EndScrollable | null>(null);
  const following = useRef(true);
  const dragging = useRef(false);
  const coasting = useRef(false);

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
      following.current = followAfterScroll(following.current, metricsOf(event), true);
    };
    const keepUp = () => {
      if (following.current) list.current?.scrollToEnd({ animated: false });
    };
    return {
      onScroll: (event) => {
        const byUser = dragging.current || coasting.current;
        following.current = followAfterScroll(following.current, metricsOf(event), byUser);
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
      onContentSizeChange: keepUp,
      onLayout: keepUp,
      scrollEventThrottle: 64,
    };
  }, [onScroll]);

  return { attach, follow, props };
}
