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
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

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

function toneBackground(palette: Palette, tone: ButtonTone): string {
  switch (tone) {
    case 'primary':
      return palette.accent;
    case 'neutral':
      return palette.surfaceRaised;
    case 'danger':
      return palette.danger;
    case 'ghost':
      return 'transparent';
  }
}

function toneText(palette: Palette, tone: ButtonTone): string {
  switch (tone) {
    case 'primary':
    case 'danger':
      return palette.onAccent;
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
        tone === 'ghost' && styles.buttonGhost,
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
  onPress,
}: {
  label: string;
  color?: string;
  selected?: boolean;
  disabled?: boolean;
  onPress?: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const tint = color ?? palette.textMuted;
  const content = (
    <View
      style={[
        styles.chip,
        { borderColor: tint },
        selected && { backgroundColor: tint, borderColor: tint },
        disabled && styles.chipDisabled,
      ]}
    >
      <Text style={[styles.chipLabel, { color: selected ? palette.onBadge : tint }]}>{label}</Text>
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

export function Badge({ count, color }: { count: number; color?: string }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  if (count <= 0) return null;
  return (
    <View style={[styles.badge, { backgroundColor: color ?? palette.accent }]}>
      <Text style={styles.badgeLabel}>{count}</Text>
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
    buttonGhost: {
      borderColor: palette.border,
      borderWidth: StyleSheet.hairlineWidth,
    },
    buttonDisabled: { opacity: 0.4 },
    buttonPressed: { opacity: 0.75 },
    buttonLabel: { fontSize: 15, fontWeight: '600' },
    buttonLabelCompact: { fontSize: 13 },
    card: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      padding: spacing.lg,
    },
    chip: {
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      paddingHorizontal: spacing.sm,
      paddingVertical: 3,
    },
    chipDisabled: { opacity: 0.4 },
    chipLabel: { fontSize: 12, fontWeight: '600' },
    badge: {
      alignItems: 'center',
      borderRadius: 9,
      justifyContent: 'center',
      minWidth: 18,
      paddingHorizontal: 5,
      paddingVertical: 1,
    },
    badgeLabel: { color: palette.onBadge, fontSize: 11, fontWeight: '700' },
    empty: { alignItems: 'center', gap: spacing.sm, padding: spacing.xl },
    emptyTitle: { color: palette.textMuted, fontSize: 15, fontWeight: '600' },
    emptyDescription: { color: palette.textFaint, fontSize: 13, textAlign: 'center' },
    errorBanner: {
      backgroundColor: palette.dangerSurface,
      borderRadius: radius.md,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      padding: spacing.md,
    },
    errorBannerText: { color: palette.danger, fontSize: 13 },
  });
