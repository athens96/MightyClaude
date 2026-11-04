import { Children, Fragment, type ReactNode } from 'react';
import { Pressable, StyleSheet, Switch, Text, View } from 'react-native';
import { Icon } from '@/components/icons';
import { cardShadow, dialog, settingsLayout, spacing, useStyles, usePalette, type Palette } from '@/theme';

/*
 * The settings page's one pattern (`.omc/plans/popup-standard.md` §2): an accent section
 * title above a card, and inside it rows of one shape, a hairline between them.
 */

/** A section: its title above the card, the rows inside it, an optional note under it. */
export function SettingsSection({
  title,
  footnote,
  children,
}: {
  title: string;
  footnote?: string;
  children: ReactNode;
}) {
  const styles = useStyles(makeStyles);
  const rows = Children.toArray(children);
  return (
    <View style={styles.section}>
      <Text accessibilityRole="header" style={styles.sectionTitle}>
        {title}
      </Text>
      <View style={styles.card}>
        {rows.map((row, index) => (
          <Fragment key={index}>
            {index > 0 ? <View style={styles.divider} /> : null}
            {row}
          </Fragment>
        ))}
      </View>
      {footnote ? <Text style={styles.footnote}>{footnote}</Text> : null}
    </View>
  );
}

/**
 * One row, at least `dialog.rowHeight` tall: the name (and a caption under it) on the left,
 * on the right the control (`children`), a value in the secondary ink, or, for a row that
 * goes somewhere (`onPress`), a chevron.
 */
export function SettingRow({
  label,
  caption,
  warning,
  value,
  onPress,
  children,
}: {
  label: string;
  caption?: string;
  /** The caption is a warning (a permission the phone has turned off). */
  warning?: boolean;
  value?: string;
  onPress?: () => void;
  children?: ReactNode;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const content = (
    <>
      <View style={styles.texts}>
        <Text style={styles.name}>{label}</Text>
        {caption ? <Text style={[styles.caption, warning && { color: palette.warning }]}>{caption}</Text> : null}
      </View>
      {children}
      {value !== undefined ? <Text style={styles.value}>{value}</Text> : null}
      {onPress ? <Icon name="chevronRight" color={palette.textFaint} size={dialog.iconSize} /> : null}
    </>
  );
  if (!onPress) return <View style={styles.row}>{content}</View>;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityHint={caption}
      onPress={onPress}
      style={({ pressed }) => [styles.row, pressed && { backgroundColor: palette.surfaceRaised }]}
    >
      {content}
    </Pressable>
  );
}

/** A row whose control is a switch. */
export function SettingSwitch({
  label,
  caption,
  value,
  onChange,
}: {
  label: string;
  /** Shown under the name and read out with the switch. */
  caption?: string;
  value: boolean;
  onChange: (value: boolean) => void;
}) {
  const palette = usePalette();
  return (
    <SettingRow label={label} caption={caption}>
      <Switch
        value={value}
        onValueChange={onChange}
        trackColor={{ false: palette.track, true: palette.accent }}
        accessibilityLabel={label}
        accessibilityHint={caption}
      />
    </SettingRow>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    section: { gap: spacing.lg },
    sectionTitle: {
      color: palette.accent,
      fontSize: settingsLayout.sectionTitle,
      fontWeight: '600',
      paddingHorizontal: settingsLayout.cardPaddingX,
    },
    card: {
      ...cardShadow,
      backgroundColor: palette.surface,
      borderRadius: settingsLayout.cardRadius,
      overflow: 'hidden',
    },
    divider: {
      backgroundColor: palette.border,
      height: StyleSheet.hairlineWidth,
      marginLeft: settingsLayout.rowDividerInset,
    },
    row: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.xl,
      minHeight: dialog.rowHeight,
      paddingHorizontal: settingsLayout.cardPaddingX,
      paddingVertical: spacing.md,
    },
    texts: { flex: 1, gap: spacing.xs },
    name: { color: palette.text, fontSize: settingsLayout.rowName },
    caption: { color: palette.textMuted, fontSize: settingsLayout.rowCaption, lineHeight: 16 },
    value: { color: palette.textMuted, fontSize: settingsLayout.rowName },
    footnote: {
      color: palette.textMuted,
      fontSize: settingsLayout.rowCaption,
      lineHeight: 16,
      paddingHorizontal: settingsLayout.cardPaddingX,
    },
  });
