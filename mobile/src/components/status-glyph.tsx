import { useEffect, useRef, useSyncExternalStore } from 'react';
import { AccessibilityInfo, Animated, Easing, View } from 'react-native';
import Svg, { Circle, G, Path, Rect } from 'react-native-svg';
import { statusGlyphFor, statusWord, type GlyphShape } from '@/lib/status-glyph';
import { usePalette, type Palette } from '@/theme';

/**
 * The system "reduce motion" switch, held once for every glyph on screen: one
 * AccessibilityInfo listener while any spark is mounted, none once the last one goes.
 */
let reduceMotion = false;
const reduceMotionListeners = new Set<() => void>();
let reduceMotionSubscription: { remove: () => void } | undefined;

function setReduceMotion(value: boolean): void {
  if (value === reduceMotion) return;
  reduceMotion = value;
  reduceMotionListeners.forEach((listener) => listener());
}

function subscribeReduceMotion(listener: () => void): () => void {
  reduceMotionListeners.add(listener);
  if (!reduceMotionSubscription) {
    reduceMotionSubscription = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduceMotion);
    // Read afresh on every first subscriber: the switch may have moved while none listened.
    void AccessibilityInfo.isReduceMotionEnabled().then(setReduceMotion);
  }
  return () => {
    reduceMotionListeners.delete(listener);
    if (reduceMotionListeners.size === 0) {
      reduceMotionSubscription?.remove();
      reduceMotionSubscription = undefined;
    }
  };
}

function useReduceMotion(): boolean {
  return useSyncExternalStore(subscribeReduceMotion, () => reduceMotion);
}

/** The eight-armed spark, turning once every 3.2 s; still when motion is reduced. */
function Spark({ color, size }: { color: string; size: number }) {
  const reduce = useReduceMotion();
  const turn = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    if (reduce) {
      turn.setValue(0);
      return undefined;
    }
    const animation = Animated.loop(
      Animated.timing(turn, {
        toValue: 1,
        duration: 3200,
        easing: Easing.linear,
        useNativeDriver: true,
      }),
    );
    animation.start();
    return () => animation.stop();
  }, [reduce, turn]);
  return (
    <Animated.View
      style={{
        height: size,
        width: size,
        transform: [{ rotate: turn.interpolate({ inputRange: [0, 1], outputRange: ['0deg', '360deg'] }) }],
      }}
    >
      <Svg height={size} viewBox="0 0 16 16" width={size}>
        <G fill="none" stroke={color} strokeLinecap="round" strokeWidth={2}>
          <Path d="M8 1.6v4M8 10.4v4M1.6 8h4M10.4 8h4" />
          <Path d="M4.6 4.6l1.5 1.5M9.9 9.9l1.5 1.5M4.6 11.4l1.5-1.5M9.9 6.1l1.5-1.5" opacity={0.7} />
        </G>
      </Svg>
    </Animated.View>
  );
}

/** Every shape but the spark: 16-unit status marks, or the pane's own 24-unit line glyph. */
function StillGlyph({ shape, palette, size }: { shape: Exclude<GlyphShape, 'spark'>; palette: Palette; size: number }) {
  const line = {
    fill: 'none',
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    strokeWidth: 1.8,
  };
  switch (shape) {
    case 'question':
      return (
        <Svg height={size} viewBox="0 0 16 16" width={size}>
          <Circle cx={8} cy={8} fill={palette.wait} r={7.2} />
          <Path
            {...line}
            d="M5.9 6.1a2.1 2.1 0 1 1 2.9 1.95c-.5.22-.8.6-.8 1.15v.35"
            stroke={palette.onWait}
            strokeWidth={1.7}
          />
          <Circle cx={8} cy={11.7} fill={palette.onWait} r={1.05} />
        </Svg>
      );
    case 'alert':
      return (
        <Svg height={size} viewBox="0 0 16 16" width={size}>
          <Circle cx={8} cy={8} fill={palette.err} r={7.2} />
          <Path {...line} d="M8 4.3v4.6" stroke={palette.onStatus} strokeWidth={1.9} />
          <Circle cx={8} cy={11.6} fill={palette.onStatus} r={1.05} />
        </Svg>
      );
    case 'check':
      return (
        <Svg height={size} viewBox="0 0 16 16" width={size}>
          <Path {...line} d="M3.2 8.5l3 3 6.6-7" stroke={palette.markDone} />
        </Svg>
      );
    case 'slashedRing':
      return (
        <Svg height={size} viewBox="0 0 16 16" width={size}>
          <Circle {...line} cx={8} cy={8} r={6} stroke={palette.markStop} />
          <Path {...line} d="M3.9 12.1l8.2-8.2" stroke={palette.markStop} />
        </Svg>
      );
    case 'ring':
      return (
        <Svg height={size} viewBox="0 0 16 16" width={size}>
          <Circle {...line} cx={8} cy={8} r={3.6} stroke={palette.textMuted} />
        </Svg>
      );
    case 'terminal':
    case 'globe': {
      // The pane's own glyph sits a little inside the box, as in the mockup (16 in 18).
      const inner = Math.round(size * 0.9);
      const stroke = { ...line, stroke: palette.textMuted, strokeWidth: 2 };
      return (
        <Svg height={inner} viewBox="0 0 24 24" width={inner}>
          {shape === 'terminal' ? (
            <>
              <Rect {...stroke} height={15} rx={3} width={18} x={3} y={4.5} />
              <Path {...stroke} d="M7.5 10l3 2.5-3 2.5M12.5 15.5h4" />
            </>
          ) : (
            <>
              <Circle {...stroke} cx={12} cy={12} r={8.5} />
              <Path
                {...stroke}
                d="M3.5 12h17M12 3.5c2.6 2.6 3.6 5.4 3.6 8.5s-1 5.9-3.6 8.5c-2.6-2.6-3.6-5.4-3.6-8.5s1-5.9 3.6-8.5z"
              />
            </>
          )}
        </Svg>
      );
    }
  }
}

/**
 * A pane's status as one small glyph — the session list's rows and the conversation
 * header both draw it, so the two never disagree. `status` is the status shown
 * (`displayStatus`, which says `waiting` while the pane holds a request); `kind` picks
 * an idle pane's own glyph. The glyph is labelled with the status word, unless it is
 * `decorative`: inside a row or header whose own label already says the status, so a
 * screen reader does not read it twice.
 */
export function StatusGlyph({
  status,
  kind,
  size = 14,
  decorative = false,
}: {
  status: string;
  kind?: string;
  size?: number;
  decorative?: boolean;
}) {
  const palette = usePalette();
  const { shape } = statusGlyphFor(status, kind);
  return (
    <View
      {...(decorative
        ? {
            accessible: false,
            accessibilityElementsHidden: true,
            importantForAccessibility: 'no-hide-descendants' as const,
          }
        : { accessible: true, accessibilityRole: 'image' as const, accessibilityLabel: statusWord(status) })}
      style={{ alignItems: 'center', height: size, justifyContent: 'center', width: size }}
    >
      {shape === 'spark' ? (
        <Spark color={palette.markRun} size={size} />
      ) : (
        <StillGlyph palette={palette} shape={shape} size={size} />
      )}
    </View>
  );
}
