import { useEffect, useRef } from 'react';
import { Animated, Easing, StyleSheet, Text, View, type StyleProp, type ViewStyle } from 'react-native';
import { useBusyState, usePending } from '@/hooks/use-pending';
import { useReduceMotion } from '@/hooks/use-reduce-motion';
import { t } from '@/lib/i18n';
import type { PendingState } from '@/lib/pending';
import { spacing, useStyles, usePalette, type Palette } from '@/theme';

const BARS = [0, 1, 2, 3] as const;
/** One cycle of the bars, the Mac's `MightyGraphActivityStyle.period` (the Windows page's 1.6 s). */
const PERIOD = 1600;
const SAMPLES = 16;
const LOOP_STOPS = Array.from({ length: SAMPLES + 1 }, (_, index) => index / SAMPLES);
/**
 * Each bar's height over one cycle as a share of its full height: the Mac's `barHeight` (4 to 12 pt, a sine a fifth of a cycle
 * after the bar before it). A sine ends where it starts, so the loop never jumps.
 */
export function barCurve(bar: number): number[] {
  return LOOP_STOPS.map((stop) => (4 + 8 * ((Math.sin((stop + bar / 5) * 2 * Math.PI) + 1) / 2)) / 12);
}
const BAR_CURVES = BARS.map(barCurve);

/**
 * The shared loading mark (natural submit A3), the Mac's activity bars: four short bars in the run tint that rise in turn, as
 * on the Mac and the Windows page. Under Reduce Motion they stand still; when they show and hide is the caller's (`<Pending>`),
 * never this.
 */
export function PendingBars({ color, height = 12 }: { color?: string; height?: number }) {
  const palette = usePalette();
  const reduce = useReduceMotion();
  const phase = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    if (reduce) {
      phase.setValue(0);
      return undefined;
    }
    const animation = Animated.loop(
      Animated.timing(phase, {
        toValue: 1,
        duration: PERIOD,
        easing: Easing.linear,
        useNativeDriver: true,
      }),
    );
    animation.start();
    return () => animation.stop();
  }, [phase, reduce]);
  const ink = color ?? palette.run;
  return (
    <View accessibilityElementsHidden importantForAccessibility="no-hide-descendants" style={[barStyles.row, { height }]}>
      {BARS.map((bar) => {
        // Each bar's sine runs a fifth of the loop after the one before it.
        const scaleY = reduce
          ? 0.6
          : phase.interpolate({
              inputRange: LOOP_STOPS,
              outputRange: BAR_CURVES[bar]!,
              extrapolate: 'clamp',
            });
        return (
          <Animated.View
            key={bar}
            style={[barStyles.bar, { backgroundColor: ink, height, transform: [{ scaleY }] }]}
          />
        );
      })}
    </View>
  );
}

const barStyles = StyleSheet.create({
  // The Mac's gap, Spacing.xxs (2 pt), which is the phone's `xs`.
  row: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
  bar: { borderRadius: 1.5, width: 3 },
});

/**
 * One pending mark for every control that waits on the host (A3): nothing for the first `pendingShowAfter`, then the bars with
 * "전송 중" (or `label`) for at least `pendingMinVisible`, and a failure's message in place at once. Pass the action's `state`,
 * or a `busy` flag the screen already keeps. `showsFailure={false}` leaves the failure to the caller (a toast, a card's line).
 */
export function Pending({
  state,
  busy,
  label,
  showsLabel = true,
  showsFailure = true,
  color,
  style,
}: {
  state?: PendingState;
  busy?: boolean;
  label?: string;
  showsLabel?: boolean;
  showsFailure?: boolean;
  color?: string;
  style?: StyleProp<ViewStyle>;
}) {
  const styles = useStyles(makeStyles);
  const busyState = useBusyState(busy ?? false);
  const indicator = usePending(state ?? busyState);
  if (indicator.kind === 'hidden') return null;
  if (indicator.kind === 'failed') {
    if (!showsFailure) return null;
    return (
      <View accessibilityLiveRegion="polite" style={[styles.row, style]}>
        <Text style={styles.failure}>{indicator.message}</Text>
      </View>
    );
  }
  const words = label ?? t('graph.state.sending');
  return (
    <View
      accessibilityLabel={words}
      accessibilityLiveRegion="polite"
      accessibilityRole="progressbar"
      style={[styles.row, style]}
    >
      <PendingBars {...(color ? { color } : {})} />
      {showsLabel ? <Text style={[styles.label, color ? { color } : null]}>{words}</Text> : null}
    </View>
  );
}

/** Whether `state` shows anything now: a control swaps its label or icon for the bars only while this is true. */
export function usePendingShown(state: PendingState): boolean {
  return usePending(state).kind === 'loading';
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    row: { alignItems: 'center', flexDirection: 'row', gap: spacing.md },
    label: { color: palette.textMuted, fontSize: 12, fontWeight: '600' },
    failure: { color: palette.danger, flexShrink: 1, fontSize: 12, lineHeight: 16 },
  });
