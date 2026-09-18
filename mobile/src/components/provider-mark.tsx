import { StyleSheet, Text, View } from 'react-native';
import Svg, { Defs, LinearGradient, Path, Stop } from 'react-native-svg';
import { providerMarkOutline } from '@/lib/provider-marks';
import { providerColorsFor, providerLabel, radius, spacing, usePalette } from '@/theme';

/**
 * The providers' own marks in their brand colours — the same outlines the Mac app draws
 * (`@/lib/provider-marks`), rendered in the 24×24 box they were prepared in. Gemini's
 * mark is a gradient through its three stops, bottom-left to top-right. A provider we do
 * not know has no outline, so it keeps a neutral chip.
 */
export function ProviderMark({ provider, size = 12 }: { provider: string; size?: number }) {
  const palette = usePalette();
  const outline = providerMarkOutline(provider);
  const colors = providerColorsFor(palette, provider);
  const [first = palette.textMuted] = colors;
  return (
    <View accessibilityElementsHidden importantForAccessibility="no">
      {outline === undefined ? (
        <View
          style={{
            backgroundColor: first,
            borderRadius: size / 3,
            height: size,
            width: size,
          }}
        />
      ) : (
        <Svg height={size} viewBox="0 0 24 24" width={size}>
          {colors.length > 1 ? (
            <Defs>
              <LinearGradient id={`provider-mark-${provider}`} x1="0" x2="1" y1="1" y2="0">
                {colors.map((color, index) => (
                  <Stop key={color} offset={index / (colors.length - 1)} stopColor={color} />
                ))}
              </LinearGradient>
            </Defs>
          ) : null}
          <Path
            d={outline}
            fill={colors.length > 1 ? `url(#provider-mark-${provider})` : first}
          />
        </Svg>
      )}
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
