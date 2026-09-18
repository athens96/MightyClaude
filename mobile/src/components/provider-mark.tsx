import { StyleSheet, Text, View } from 'react-native';
import { providerColorsFor, providerLabel, radius, spacing, usePalette } from '@/theme';

/**
 * The providers' marks in their brand colours. The Mac app draws the real outlines
 * (`MightyCore/ProviderMark.swift`), which needs a vector renderer; this app has no
 * `react-native-svg` dependency and adds none, so the mark is a brand-coloured chip.
 * Gemini's gradient becomes its three stops stacked, start to end.
 */
export function ProviderMark({ provider, size = 12 }: { provider: string; size?: number }) {
  const palette = usePalette();
  const bands = providerColorsFor(palette, provider);
  return (
    <View
      accessibilityElementsHidden
      importantForAccessibility="no"
      style={{ borderRadius: size / 3, height: size, overflow: 'hidden', width: size }}
    >
      {bands.map((color) => (
        <View key={color} style={[styles.band, { backgroundColor: color }]} />
      ))}
    </View>
  );
}

/** Mark plus the provider's name, tinted with its first brand colour. */
export function ProviderTag({ provider }: { provider: string }) {
  const palette = usePalette();
  const [tint = palette.textMuted] = providerColorsFor(palette, provider);
  return (
    <View style={[styles.tag, { borderColor: tint }]}>
      <ProviderMark provider={provider} size={10} />
      <Text style={[styles.tagLabel, { color: tint }]}>{providerLabel(provider)}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  band: { flex: 1, width: '100%' },
  tag: {
    alignItems: 'center',
    borderRadius: radius.sm,
    borderWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    gap: spacing.xs,
    paddingHorizontal: spacing.sm,
    paddingVertical: 2,
  },
  tagLabel: { fontSize: 11, fontWeight: '700' },
});
