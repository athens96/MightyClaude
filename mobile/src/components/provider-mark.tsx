import { memo } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import Svg, { Defs, LinearGradient, Path, Stop } from 'react-native-svg';
import { providerMarkOutline } from '@/lib/provider-marks';
import { BetaBadge } from '@/components/ui';
import { providerColorsFor, providerIsBeta, providerLabel, spacing, usePalette } from '@/theme';

/**
 * The providers' own marks in their brand colours — the same outlines the Mac app draws
 * (`@/lib/provider-marks`), rendered in the 24×24 box they were prepared in. Gemini's
 * mark is a gradient through its three stops, bottom-left to top-right. A provider we do
 * not know has no outline, so it keeps a neutral chip.
 */
export const ProviderMark = memo(function ProviderMark({
  provider,
  size = 12,
  color,
}: {
  provider: string;
  size?: number;
  /** One colour for the whole mark, e.g. white on a brand-coloured avatar square. */
  color?: string;
}) {
  const palette = usePalette();
  const outline = providerMarkOutline(provider);
  const colors = color ? [color] : providerColorsFor(palette, provider);
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
});

/**
 * Mark plus the provider's name, then the Beta badge for a beta provider. The brand
 * colour stays on the mark; the name is muted ink, as every other meta word on the row.
 */
export function ProviderTag({ provider }: { provider: string }) {
  const palette = usePalette();
  return (
    <View style={styles.tag}>
      <ProviderMark provider={provider} size={11} />
      <Text style={[styles.tagLabel, { color: palette.textMuted }]}>{providerLabel(provider)}</Text>
      {providerIsBeta(provider) ? <BetaBadge /> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  tag: {
    alignItems: 'center',
    flexDirection: 'row',
    gap: spacing.xs,
  },
  tagLabel: { fontSize: 12, fontWeight: '500' },
});
