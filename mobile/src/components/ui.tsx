import type { ReactNode } from 'react';
import {
  ActivityIndicator,
  Pressable,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import * as Haptics from 'expo-haptics';
import { t } from '@/lib/i18n';
import { radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

export type ButtonTone = 'primary' | 'neutral' | 'danger' | 'ghost';

interface ButtonProps {
  label: string;
  onPress: () => void;
  tone?: ButtonTone;
  disabled?: boolean;
  busy?: boolean;
  compact?: boolean;
  style?: StyleProp<ViewStyle>;
  /** Read out instead of the label, for buttons whose label alone says too little. */
  accessibilityLabel?: string;
}

/**
 * One filled button — the terracotta primary. Neutral is an outline, ghost is plain text,
 * and danger is brick text on its own soft tint rather than a second loud fill.
 */
function toneBackground(palette: Palette, tone: ButtonTone): string {
  switch (tone) {
    case 'primary':
      return palette.accent;
    case 'neutral':
      return 'transparent';
    case 'danger':
      return palette.dangerSurface;
    case 'ghost':
      return 'transparent';
  }
}

function toneText(palette: Palette, tone: ButtonTone): string {
  switch (tone) {
    case 'primary':
      return palette.onAccent;
    case 'danger':
      return palette.danger;
    case 'neutral':
      return palette.text;
    case 'ghost':
      return palette.textMuted;
  }
}

export function Button({
  label,
  onPress,
  tone = 'neutral',
  disabled = false,
  busy = false,
  compact = false,
  style,
  accessibilityLabel,
}: ButtonProps) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const inactive = disabled || busy;
  return (
    <Pressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ disabled: inactive }}
      disabled={inactive}
      onPress={() => {
        void Haptics.selectionAsync();
        onPress();
      }}
      style={({ pressed }) => [
        styles.button,
        compact && styles.buttonCompact,
        { backgroundColor: toneBackground(palette, tone) },
        tone === 'neutral' && styles.buttonOutline,
        inactive && styles.buttonDisabled,
        pressed && styles.buttonPressed,
        style,
      ]}
    >
      {busy ? (
        <ActivityIndicator color={toneText(palette, tone)} size="small" />
      ) : (
        <Text
          style={[
            styles.buttonLabel,
            compact && styles.buttonLabelCompact,
            { color: toneText(palette, tone) },
          ]}
        >
          {label}
        </Text>
      )}
    </Pressable>
  );
}

export function Card({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  const styles = useStyles(makeStyles);
  return <View style={[styles.card, style]}>{children}</View>;
}

export function Chip({
  label,
  color,
  selected = false,
  disabled = false,
  beta = false,
  onPress,
}: {
  label: string;
  color?: string;
  selected?: boolean;
  disabled?: boolean;
  /** Draws the Beta badge after the label (see `providerIsBeta`). */
  beta?: boolean;
  onPress?: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const tint = color ?? palette.textMuted;
  // Quiet by default: a hairline and muted text. Selected earns the tint as a border, a
  // soft wash and a leading dot — the dot, so the state never rests on colour alone.
  const content = (
    <View
      style={[
        styles.chip,
        selected && { backgroundColor: palette.accentMuted, borderColor: tint },
        disabled && styles.chipDisabled,
      ]}
    >
      {selected ? <View style={[styles.chipDot, { backgroundColor: tint }]} /> : null}
      <Text style={[styles.chipLabel, selected && styles.chipLabelSelected]}>{label}</Text>
      {beta ? <BetaBadge /> : null}
    </View>
  );
  if (!onPress) return content;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected, disabled }}
      disabled={disabled}
      onPress={() => {
        void Haptics.selectionAsync();
        onPress();
      }}
    >
      {content}
    </Pressable>
  );
}

/** The small capsule drawn after a beta provider's name; never part of the name itself. */
export function BetaBadge() {
  const styles = useStyles(makeStyles);
  return (
    <View
      accessible
      accessibilityLabel={t('badge.betaAccessibility')}
      style={styles.betaBadge}
    >
      <Text style={styles.betaBadgeLabel}>{t('badge.beta')}</Text>
    </View>
  );
}

/** A count that needs the user: a dot and the number beside it, never a filled pill. */
export function Badge({ count, color }: { count: number; color?: string }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  if (count <= 0) return null;
  const tint = color ?? palette.accent;
  return (
    <View style={styles.badge}>
      <View style={[styles.badgeDot, { backgroundColor: tint }]} />
      <Text style={[styles.badgeLabel, { color: tint }]}>{count}</Text>
    </View>
  );
}

export function EmptyState({ title, description }: { title: string; description?: string }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.empty}>
      <Text style={styles.emptyTitle}>{title}</Text>
      {description ? <Text style={styles.emptyDescription}>{description}</Text> : null}
    </View>
  );
}

export function ErrorBanner({ message }: { message: string }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.errorBanner}>
      <Text style={styles.errorBannerText}>{message}</Text>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    button: {
      alignItems: 'center',
      borderRadius: radius.md,
      justifyContent: 'center',
      minHeight: 44,
      paddingHorizontal: spacing.lg,
    },
    buttonCompact: {
      minHeight: 34,
      paddingHorizontal: spacing.md,
    },
    buttonOutline: {
      borderColor: palette.border,
      borderWidth: StyleSheet.hairlineWidth,
    },
    buttonDisabled: { opacity: 0.4 },
    buttonPressed: { opacity: 0.7 },
    buttonLabel: { fontSize: 15, fontWeight: '600' },
    buttonLabelCompact: { fontSize: 14 },
    // A flat sheet of slightly lighter paper: no border, no shadow.
    card: {
      backgroundColor: palette.surface,
      borderRadius: radius.lg,
      padding: spacing.lg,
    },
    chip: {
      alignItems: 'center',
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: spacing.xs,
      paddingHorizontal: spacing.sm,
      paddingVertical: 3,
    },
    chipDisabled: { opacity: 0.4 },
    chipDot: { borderRadius: radius.round, height: 5, width: 5 },
    chipLabel: { color: palette.textMuted, fontSize: 12, fontWeight: '500' },
    chipLabelSelected: { color: palette.text, fontWeight: '600' },
    badge: { alignItems: 'center', flexDirection: 'row', gap: 3 },
    badgeDot: { borderRadius: radius.round, height: 6, width: 6 },
    badgeLabel: { fontSize: 12, fontVariant: ['tabular-nums'], fontWeight: '600' },
    betaBadge: {
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      paddingHorizontal: 4,
    },
    betaBadgeLabel: { color: palette.textMuted, fontSize: 9, fontWeight: '500', letterSpacing: 0.3 },
    empty: { alignItems: 'center', gap: spacing.sm, paddingHorizontal: spacing.xl, paddingVertical: spacing.xxl },
    emptyTitle: { ...typeScale.heading, color: palette.text, textAlign: 'center' },
    emptyDescription: { color: palette.textMuted, fontSize: 14, lineHeight: 20, textAlign: 'center' },
    errorBanner: {
      backgroundColor: palette.dangerSurface,
      borderRadius: radius.md,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    errorBannerText: { color: palette.danger, fontSize: 13, lineHeight: 18 },
  });
