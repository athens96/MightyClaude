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
import { colors, radius, spacing } from '@/theme';

export type ButtonTone = 'primary' | 'neutral' | 'danger' | 'ghost';

interface ButtonProps {
  label: string;
  onPress: () => void;
  tone?: ButtonTone;
  disabled?: boolean;
  busy?: boolean;
  compact?: boolean;
  style?: StyleProp<ViewStyle>;
}

const toneBackground: Record<ButtonTone, string> = {
  primary: colors.accent,
  neutral: colors.surfaceRaised,
  danger: colors.danger,
  ghost: 'transparent',
};

const toneText: Record<ButtonTone, string> = {
  primary: '#1a0f08',
  neutral: colors.text,
  danger: '#1a0f08',
  ghost: colors.textMuted,
};

export function Button({
  label,
  onPress,
  tone = 'neutral',
  disabled = false,
  busy = false,
  compact = false,
  style,
}: ButtonProps) {
  const inactive = disabled || busy;
  return (
    <Pressable
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
        { backgroundColor: toneBackground[tone] },
        tone === 'ghost' && styles.buttonGhost,
        inactive && styles.buttonDisabled,
        pressed && styles.buttonPressed,
        style,
      ]}
    >
      {busy ? (
        <ActivityIndicator color={toneText[tone]} size="small" />
      ) : (
        <Text style={[styles.buttonLabel, compact && styles.buttonLabelCompact, { color: toneText[tone] }]}>
          {label}
        </Text>
      )}
    </Pressable>
  );
}

export function Card({ children, style }: { children: ReactNode; style?: StyleProp<ViewStyle> }) {
  return <View style={[styles.card, style]}>{children}</View>;
}

export function Chip({
  label,
  color = colors.textMuted,
  selected = false,
  onPress,
}: {
  label: string;
  color?: string;
  selected?: boolean;
  onPress?: () => void;
}) {
  const content = (
    <View
      style={[
        styles.chip,
        { borderColor: color },
        selected && { backgroundColor: color, borderColor: color },
      ]}
    >
      <Text style={[styles.chipLabel, { color: selected ? '#14140f' : color }]}>{label}</Text>
    </View>
  );
  if (!onPress) return content;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={() => {
        void Haptics.selectionAsync();
        onPress();
      }}
    >
      {content}
    </Pressable>
  );
}

export function Badge({ count, color = colors.accent }: { count: number; color?: string }) {
  if (count <= 0) return null;
  return (
    <View style={[styles.badge, { backgroundColor: color }]}>
      <Text style={styles.badgeLabel}>{count}</Text>
    </View>
  );
}

export function EmptyState({ title, description }: { title: string; description?: string }) {
  return (
    <View style={styles.empty}>
      <Text style={styles.emptyTitle}>{title}</Text>
      {description ? <Text style={styles.emptyDescription}>{description}</Text> : null}
    </View>
  );
}

export function ErrorBanner({ message }: { message: string }) {
  return (
    <View style={styles.errorBanner}>
      <Text style={styles.errorBannerText}>{message}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
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
    borderColor: colors.border,
    borderWidth: StyleSheet.hairlineWidth,
  },
  buttonDisabled: { opacity: 0.4 },
  buttonPressed: { opacity: 0.75 },
  buttonLabel: { fontSize: 15, fontWeight: '600' },
  buttonLabelCompact: { fontSize: 13 },
  card: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
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
  chipLabel: { fontSize: 12, fontWeight: '600' },
  badge: {
    alignItems: 'center',
    borderRadius: 9,
    justifyContent: 'center',
    minWidth: 18,
    paddingHorizontal: 5,
    paddingVertical: 1,
  },
  badgeLabel: { color: '#14140f', fontSize: 11, fontWeight: '700' },
  empty: { alignItems: 'center', gap: spacing.sm, padding: spacing.xl },
  emptyTitle: { color: colors.textMuted, fontSize: 15, fontWeight: '600' },
  emptyDescription: { color: colors.textFaint, fontSize: 13, textAlign: 'center' },
  errorBanner: {
    backgroundColor: '#2a1614',
    borderRadius: radius.md,
    marginHorizontal: spacing.lg,
    marginTop: spacing.sm,
    padding: spacing.md,
  },
  errorBannerText: { color: colors.danger, fontSize: 13 },
});
