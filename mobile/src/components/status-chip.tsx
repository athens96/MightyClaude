import { useEffect, useRef } from 'react';
import { Animated, Easing, StyleSheet, Text, View } from 'react-native';
import { radius, spacing, statusColor, statusLabel, usePalette } from '@/theme';

/**
 * Status pill; the running state pulses so activity is visible at a glance. The status
 * arrives as a string: one the contract does not list is drawn in the neutral colour
 * with its own text rather than being forced into a known state.
 */
export function StatusChip({ status }: { status: string }) {
  const palette = usePalette();
  const pulse = useRef(new Animated.Value(1)).current;

  useEffect(() => {
    if (status !== 'running') {
      pulse.setValue(1);
      return undefined;
    }
    const animation = Animated.loop(
      Animated.sequence([
        Animated.timing(pulse, {
          toValue: 0.35,
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
  }, [pulse, status]);

  const color = statusColor(palette, status);
  return (
    <View style={[styles.chip, { borderColor: color }]}>
      <Animated.View style={[styles.dot, { backgroundColor: color, opacity: pulse }]} />
      <Text style={[styles.label, { color }]}>{statusLabel(status)}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  chip: {
    alignItems: 'center',
    borderRadius: radius.sm,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    gap: spacing.xs,
    paddingHorizontal: spacing.sm,
    paddingVertical: 3,
  },
  dot: { borderRadius: 3, height: 6, width: 6 },
  label: { fontSize: 11, fontWeight: '700' },
});
