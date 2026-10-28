import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { MobileBackgroundWork } from '@/api/types';
import { RunHead } from '@/components/background-run';
import { formatDuration } from '@/components/log-entry-view';
import { useNow } from '@/hooks/use-now';
import { backgroundElapsed, backgroundLastLine, backgroundTitle } from '@/lib/background-work';
import { t } from '@/lib/i18n';
import { BLOCK_KIND_MARKS } from '@/lib/mighty';
import { monoText, radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * The running background work of a pane, folded into one row above the Mighty view (`plan.background.listTitle`);
 * opened, one row each with its kind, name, elapsed time and last output line, and a row opened in turn
 * shows its command (or a sub-agent's first prompt line) and the last output lines. Ended work is not here:
 * each finished batch is a diagram of its own in the view.
 */
export function BackgroundStrip({ work }: { work: readonly MobileBackgroundWork[] }) {
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const now = useNow(1000, open && work.length > 0);
  if (work.length === 0) return null;
  return (
    <View style={styles.strip}>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen((value) => !value)}
        style={({ pressed }) => [styles.head, pressed && styles.pressed]}
      >
        <Text style={styles.chevron}>{open ? '▾' : '▸'}</Text>
        <Text numberOfLines={1} style={styles.headTitle}>
          {t('plan.background.listTitle', { count: work.length })}
        </Text>
      </Pressable>
      {open ? work.map((item) => <WorkRow key={item.id} item={item} now={now} />) : null}
    </View>
  );
}

function WorkRow({ item, now }: { item: MobileBackgroundWork; now: number }) {
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const elapsed = backgroundElapsed(item.startedAt, now);
  const kind = item.kind === 'run' ? 'task' : 'agent';
  const last = backgroundLastLine(item.outputTail);
  const tail = item.outputTail ?? [];
  const command = item.command ?? '';
  const detail = command.trim().length > 0 || tail.length > 0;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open }}
      onPress={() => setOpen((value) => !value)}
      style={({ pressed }) => [styles.row, pressed && styles.pressed]}
    >
      <View style={styles.rowHead}>
        <Text style={styles.mark}>{BLOCK_KIND_MARKS[kind]}</Text>
        <Text numberOfLines={1} style={styles.rowTitle}>
          {backgroundTitle(item)}
        </Text>
        {elapsed !== undefined ? <Text style={styles.elapsed}>{formatDuration(elapsed)}</Text> : null}
      </View>
      {last && !open ? (
        <Text numberOfLines={1} style={styles.lastLine}>
          {last}
        </Text>
      ) : null}
      {open && detail ? <RunHead command={command} tail={tail} open /> : null}
    </Pressable>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    strip: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.lg,
      gap: spacing.xs,
      marginHorizontal: spacing.lg,
      marginTop: spacing.sm,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    head: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm, minHeight: 44 },
    chevron: { color: palette.textFaint, fontSize: 12 },
    headTitle: { color: palette.textMuted, flex: 1, fontSize: 13, fontWeight: '600' },
    row: { gap: spacing.xs, paddingVertical: spacing.sm },
    rowHead: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    mark: { color: palette.textFaint, fontSize: 12 },
    rowTitle: { color: palette.text, flex: 1, fontSize: 13, fontWeight: '700' },
    elapsed: { color: palette.textFaint, fontSize: 11.5 },
    lastLine: { ...monoText, color: palette.textMuted },
    pressed: { opacity: 0.6 },
  });
