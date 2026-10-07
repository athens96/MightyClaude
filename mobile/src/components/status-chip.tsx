import { useEffect, useRef } from 'react';
import { Animated, Easing, StyleSheet, Text, View } from 'react-native';
import { toneOf } from '@/lib/status-tone';
import { radius, spacing, statusLabel, toneColors, usePalette } from '@/theme';

/**
 * Status as a capsule in its tone. Running and waiting are the loud ones — filled, the
 * running one with a breathing dot so activity reads at a glance; the rest sit on their
 * soft tint. The status arrives as a string: one the contract does not list is drawn in
 * the neutral tone with its own text rather than being forced into a known state.
 */
export function StatusChip({ status, label }: { status: string; label?: string }) {
  const palette = usePalette();
  const pulse = useRef(new Animated.Value(1)).current;
  const running = status === 'running';

  useEffect(() => {
    if (!running) {
      pulse.setValue(1);
      return undefined;
    }
    const animation = Animated.loop(
      Animated.sequence([
        Animated.timing(pulse, {
          toValue: 0.3,
          duration: 650,
          easing: Easing.inOut(Easing.quad),
          useNativeDriver: true,
        }),
        Animated.timing(pulse, {
          toValue: 1,
          duration: 650,
          easing: Easing.inOut(Easing.quad),
          useNativeDriver: true,
        }),
      ]),
    );
    animation.start();
    return () => animation.stop();
  }, [pulse, running]);

  const tone = toneOf(status);
  const colors = toneColors(palette, tone);
  const filled = tone === 'run' || tone === 'wait';
  const ink = filled ? colors.onFill : colors.ink;
  return (
    <View style={[styles.chip, { backgroundColor: filled ? colors.fill : colors.soft }]}>
      {running ? <Animated.View style={[styles.dot, { backgroundColor: ink, opacity: pulse }]} /> : null}
      <Text numberOfLines={1} style={[styles.label, { color: ink }]}>
        {label ?? statusLabel(status)}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  chip: {
    alignItems: 'center',
    borderRadius: radius.round,
    flexDirection: 'row',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs,
  },
  dot: { borderRadius: radius.round, height: 6, width: 6 },
  label: { fontSize: 11.5, fontWeight: '700' },
});
