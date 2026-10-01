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
import {
  cardShadow,
  radius,
  spacing,
  typeScale,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/** `ink` is the dark capsule beside a workspace (새 창): loud without spending the blue. */
export type ButtonTone = 'primary' | 'neutral' | 'danger' | 'ghost' | 'ink';

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
 * One filled button — the run-blue primary. Neutral is a white card with a hairline,
 * ghost is plain text, and danger is red ink on its own soft tint rather than a second
 * loud fill.
 */
function toneBackground(palette: Palette, tone: ButtonTone): string {
  switch (tone) {
    case 'primary':
      return palette.accent;
    case 'neutral':
      return palette.surface;
    case 'danger':
      return palette.dangerSurface;
    case 'ghost':
      return 'transparent';
    case 'ink':
      return palette.text;
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
    case 'ink':
      return palette.background;
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

/** A count that needs the user: a filled amber pill with dark ink, as on the tab bar. */
export function Badge({ count }: { count: number }) {
  const styles = useStyles(makeStyles);
  if (count <= 0) return null;
  return (
    <View style={styles.badge}>
      <Text style={styles.badgeLabel}>{count}</Text>
    </View>
  );
}

/** Two or three views of one screen: a grey track with the chosen one on a white key. */
export function SegmentedControl<T extends string>({
  options,
  value,
  onChange,
}: {
  options: readonly { id: T; label: string }[];
  value: T;
  onChange: (id: T) => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <View accessibilityRole="tablist" style={styles.segment}>
      {options.map((option) => {
        const selected = option.id === value;
        return (
          <Pressable
            key={option.id}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => {
              if (selected) return;
              void Haptics.selectionAsync();
              onChange(option.id);
            }}
            style={[styles.segmentItem, selected && styles.segmentItemSelected]}
          >
            <Text style={[styles.segmentLabel, selected && styles.segmentLabelSelected]}>
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** A screen's large title in bold Avenir Next, as on the "작업 현황" tab. */
export function ScreenTitle({ title, style }: { title: string; style?: StyleProp<ViewStyle> }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={style}>
      <Text accessibilityRole="header" style={styles.screenTitle}>
        {title}
      </Text>
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
      minHeight: 46,
      paddingHorizontal: spacing.lg,
    },
    buttonCompact: {
      borderRadius: radius.round,
      minHeight: 32,
      paddingHorizontal: spacing.md,
    },
    buttonOutline: {
      borderColor: palette.border,
      borderWidth: StyleSheet.hairlineWidth,
    },
    buttonDisabled: { opacity: 0.4 },
    buttonPressed: { opacity: 0.75, transform: [{ scale: 0.98 }] },
    buttonLabel: { fontSize: 15, fontWeight: '700' },
    buttonLabelCompact: { fontSize: 13 },
    // A white card lifting off the grey page.
    card: {
      ...cardShadow,
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      padding: spacing.lg,
    },
    chip: {
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.round,
      borderWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: spacing.xs,
      paddingHorizontal: 10,
      paddingVertical: 4,
    },
    chipDisabled: { opacity: 0.4 },
    chipDot: { borderRadius: radius.round, height: 6, width: 6 },
    chipLabel: { color: palette.textMuted, fontSize: 12, fontWeight: '600' },
    chipLabelSelected: { color: palette.text, fontWeight: '700' },
    badge: {
      alignItems: 'center',
      backgroundColor: palette.wait,
      borderRadius: radius.round,
      minWidth: 20,
      paddingHorizontal: 6,
      paddingVertical: 1,
    },
    badgeLabel: {
      color: palette.onBadge,
      fontSize: 11,
      fontVariant: ['tabular-nums'],
      fontWeight: '800',
    },
    segment: {
      backgroundColor: palette.track,
      borderRadius: radius.md,
      flexDirection: 'row',
      padding: 3,
    },
    segmentItem: {
      alignItems: 'center',
      borderRadius: 9,
      flex: 1,
      minHeight: 32,
      justifyContent: 'center',
    },
    segmentItemSelected: { ...cardShadow, backgroundColor: palette.surface },
    segmentLabel: { color: palette.textMuted, fontSize: 13, fontWeight: '700' },
    segmentLabelSelected: { color: palette.text },
    screenTitle: { ...typeScale.display, color: palette.text },
    betaBadge: {
      backgroundColor: palette.waitSoft,
      borderRadius: 4,
      paddingHorizontal: 4,
    },
    betaBadgeLabel: { color: palette.warning, fontSize: 9.5, fontWeight: '800', letterSpacing: 0.2 },
    empty: { alignItems: 'center', gap: spacing.sm, paddingHorizontal: spacing.xl, paddingVertical: spacing.xxl },
    emptyTitle: { ...typeScale.heading, color: palette.text, fontSize: 18, lineHeight: 24, textAlign: 'center' },
    emptyDescription: { color: palette.textMuted, fontSize: 14, lineHeight: 20, textAlign: 'center' },
    errorBanner: {
      backgroundColor: palette.dangerSurface,
      borderRadius: radius.md,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm + 2,
    },
    errorBannerText: { color: palette.danger, fontSize: 13, fontWeight: '600', lineHeight: 18 },
  });
