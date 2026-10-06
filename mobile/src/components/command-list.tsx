import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import type { MobileCommand } from '@/api/types';
import { t } from '@/lib/i18n';
import { radius, spacing, useStyles, type Palette } from '@/theme';

/** Locale keys, read when a row is drawn. */
const sourceLabelKeys: Record<string, string> = {
  app: 'phone.commands.source.app',
  builtin: 'phone.commands.source.builtin',
  project: 'phone.commands.source.project',
  user: 'phone.commands.source.user',
  plugin: 'phone.commands.source.plugin',
};

/**
 * Unknown sources from the host are shown as they came, never as a wrong label. The
 * lookup goes through `Object.hasOwn`: a plain object answers `constructor` with a
 * function, which `<Text>` would silently draw as nothing.
 */
function sourceLabel(source: string): string {
  const key = Object.hasOwn(sourceLabelKeys, source) ? sourceLabelKeys[source] : undefined;
  return key !== undefined ? t(key) : source;
}

/** The "/" list above the composer: filtered host commands, one tap to pick one. */
export function CommandList({
  commands,
  onSelect,
}: {
  commands: MobileCommand[];
  onSelect: (command: MobileCommand) => void;
}) {
  const styles = useStyles(makeStyles);
  if (commands.length === 0) return null;
  return (
    <View style={styles.wrap}>
      <ScrollView keyboardShouldPersistTaps="handled" style={styles.list}>
        {commands.map((command) => (
          <Pressable
            accessibilityRole="button"
            key={`${command.source}:${command.name}`}
            onPress={() => onSelect(command)}
            style={({ pressed }) => [styles.row, pressed && styles.pressed]}
          >
            <View style={styles.rowTop}>
              <Text numberOfLines={1} style={styles.name}>
                /{command.name}
              </Text>
              {command.argumentHint ? (
                <Text numberOfLines={1} style={styles.hint}>
                  {command.argumentHint}
                </Text>
              ) : null}
              <Text style={styles.source}>{sourceLabel(command.source)}</Text>
            </View>
            {command.description ? (
              <Text numberOfLines={2} style={styles.description}>
                {command.description}
              </Text>
            ) : null}
          </Pressable>
        ))}
      </ScrollView>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    wrap: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      marginBottom: spacing.xs,
      overflow: 'hidden',
    },
    list: { maxHeight: 220 },
    row: {
      borderBottomColor: palette.border,
      borderBottomWidth: StyleSheet.hairlineWidth,
      gap: 2,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    pressed: { backgroundColor: palette.surfaceRaised },
    rowTop: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    name: { color: palette.text, fontSize: 14, fontWeight: '600' },
    hint: { color: palette.textFaint, flex: 1, fontSize: 12 },
    source: { color: palette.textFaint, fontSize: 11 },
    description: { color: palette.textMuted, fontSize: 12, lineHeight: 17 },
  });
