import { StyleSheet, Text, View } from 'react-native';
import { t } from '@/lib/i18n';
import { monoText, radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * A background run's command and live output lines, set in a tinted head the way the other
 * hosts draw it; nothing at all when both are empty. `open` lets the command wrap.
 */
export function RunHead({ command, tail, open }: { command: string; tail: readonly string[]; open: boolean }) {
  const styles = useStyles(makeStyles);
  const flat = command.split(/\r?\n/).join(' ').trim();
  if (flat.length === 0 && tail.length === 0) return null;
  return (
    <View style={styles.head}>
      {flat.length > 0 ? (
        <Text numberOfLines={open ? undefined : 1} selectable style={styles.command}>
          {flat}
        </Text>
      ) : null}
      {tail.map((line, index) => (
        <Text key={index} numberOfLines={open ? undefined : 1} selectable style={styles.line}>
          {line === '' ? ' ' : line}
        </Text>
      ))}
    </View>
  );
}

/** Claude's reply to a background run's notice, under its own label; two lines folded. */
export function NoticeReply({ text, lines }: { text: string; lines: number | undefined }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.reply}>
      <Text style={styles.replyLabel}>{t('graph.background.reply')}</Text>
      <Text numberOfLines={lines} selectable style={styles.replyText}>
        {text}
      </Text>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    head: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      gap: spacing.xs,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.sm,
    },
    command: { ...monoText, color: palette.text, fontWeight: '700' },
    line: { ...monoText, color: palette.textMuted },
    reply: { gap: spacing.xs },
    replyLabel: { color: palette.textFaint, fontSize: 11, fontWeight: '700' },
    replyText: { color: palette.text, fontSize: 12.5, lineHeight: 18 },
  });
