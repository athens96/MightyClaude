import * as Haptics from 'expo-haptics';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { t } from '@/lib/i18n';
import { nextActionText, type NextAction } from '@/lib/next-actions';
import { radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * The options after `next:` in the last finished reply's `◆` breadcrumb: under that reply
 * in the log, docked above the composer in the blocks view. A tap only fills the composer
 * (after any draft already there): the user edits the text and sends it themselves.
 */
export function NextActionChips({
  actions,
  onFill,
}: {
  actions: NextAction[];
  onFill: (text: string) => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <View accessibilityLabel={t('pane.nextActions.label')} accessibilityRole="toolbar" style={styles.row}>
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
          </Pressable>
        );
      })}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    row: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs, paddingBottom: spacing.xs },
    // Suggestions in ink on a hairline: an offer, not a call to action.
    chip: {
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      maxWidth: '100%',
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.xs + 2,
    },
    label: { color: palette.text, fontSize: 13, lineHeight: 18 },
    pressed: { backgroundColor: palette.surfaceRaised },
  });
