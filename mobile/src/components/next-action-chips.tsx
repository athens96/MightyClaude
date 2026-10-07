import * as Haptics from 'expo-haptics';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { t } from '@/lib/i18n';
import { nextActionText, type NextAction } from '@/lib/next-actions';
import { Icon } from '@/components/icons';
import { cardShadow, radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The options after `next:` in the last finished reply's `◆` breadcrumb: under that reply
 * in the log, docked above the composer in the blocks view. Each is a white row with a
 * blue arrow under a small "다음 작업 제안" label. A tap only fills the composer (after any
 * draft already there): the user edits the text and sends it themselves.
 */
export function NextActionChips({
  actions,
  onFill,
}: {
  actions: NextAction[];
  onFill: (text: string) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View accessibilityLabel={t('pane.nextActions.label')} accessibilityRole="toolbar" style={styles.row}>
      <Text style={styles.heading}>{t('pane.nextActions.label')}</Text>
      {actions.map((action, index) => {
        const text = nextActionText(action);
        return (
          <Pressable
            key={`${index}:${action.label}`}
            accessibilityHint={t('pane.nextActions.fillHint')}
            accessibilityLabel={text}
            accessibilityRole="button"
            onPress={() => {
              void Haptics.selectionAsync();
              onFill(action.fill);
            }}
            style={({ pressed }) => [styles.chip, pressed && styles.pressed]}
          >
            <Text numberOfLines={2} ellipsizeMode="tail" style={styles.label}>
              {text}
            </Text>
            <Icon name="arrow" color={palette.accent} size={16} strokeWidth={2.6} />
          </Pressable>
        );
      })}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    row: { gap: spacing.sm, paddingBottom: spacing.xs, paddingTop: spacing.xs },
    heading: {
      color: palette.textFaint,
      fontSize: 11.5,
      fontWeight: '700',
      letterSpacing: 0.3,
      marginBottom: 1,
      marginHorizontal: spacing.xs,
    },
    chip: {
      ...cardShadow,
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.lg - 2,
      flexDirection: 'row',
      gap: spacing.sm,
      // About 40pt to tap, however short the label.
      minHeight: 40,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.lg,
    },
    label: { color: palette.text, flex: 1, fontSize: 13.5, fontWeight: '600', lineHeight: 18 },
    pressed: { backgroundColor: palette.surfaceRaised },
  });
