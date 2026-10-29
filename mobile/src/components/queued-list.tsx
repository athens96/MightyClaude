import { useEffect, useRef } from 'react';
import { Animated, Easing, StyleSheet, Text, View } from 'react-native';
import type { QueuedItem } from '@/api/types';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { runNowOf, runNowReasonKey } from '@/lib/queue';
import { monoText, motion, radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/** How long a queued send's flash waits for its row to be listed. */
const FLASH_WAIT_MS = 5000;

/**
 * The pane's queue. Removing an item and 바로 실행 are offered only on a host that advertised
 * "queue"; otherwise the list reads exactly as it did before. 바로 실행 sits on each row the host
 * sent a `runNow` for (an old host sends none, so it shows none): the host runs the row in a new
 * worktree child or in this pane. A row that must wait is dimmed, and a tap says why.
 */
export function QueuedList({
  items,
  manageable,
  busy,
  flash = 0,
  onRemove,
  onRunNow,
  onExplain,
}: {
  items: QueuedItem[];
  manageable: boolean;
  busy: boolean;
  /**
   * Bumped when a send from this phone was queued (natural submit B3): the list washes in
   * the run tint and fades back over `motion.highlight`. A colour change, not movement, so
   * Reduce Motion keeps it. 0 never flashes.
   */
  flash?: number;
  onRemove: (itemId: string) => void;
  /** 바로 실행 on a row the host can run now. */
  onRunNow: (item: QueuedItem) => void;
  /** A tap on a dimmed 바로 실행: the words that say why it must wait. */
  onExplain: (text: string) => void;
}) {
  const styles = useStyles(makeStyles);
  const palette = usePalette();
  const wash = useRef(new Animated.Value(0)).current;
  // The answer that said "queued" can come before the poll that lists the item, so a flash
  // waits for a row to show it on, for a few seconds at most.
  const owed = useRef<number | undefined>(undefined);
  const running = useRef<Animated.CompositeAnimation | undefined>(undefined);
  useEffect(() => {
    if (flash !== 0) owed.current = Date.now();
  }, [flash]);
  useEffect(() => {
    const since = owed.current;
    if (since === undefined || items.length === 0) return;
    owed.current = undefined;
    if (Date.now() - since > FLASH_WAIT_MS) return;
    running.current?.stop();
    wash.setValue(1);
    running.current = Animated.timing(wash, {
      toValue: 0,
      duration: motion.highlight,
      easing: Easing.bezier(...motion.easing),
      useNativeDriver: false,
    });
    running.current.start();
  }, [flash, items.length, wash]);
  useEffect(() => () => running.current?.stop(), []);
  if (items.length === 0) return null;
  const backgroundColor = wash.interpolate({ inputRange: [0, 1], outputRange: [palette.surface, palette.runSoft] });
  return (
    <Animated.View style={[styles.wrap, { backgroundColor }]}>
      <View style={styles.header}>
        <Text style={styles.title}>{t('phone.queue.title', { count: items.length })}</Text>
      </View>
      {items.map((item) => {
        const runNow = runNowOf(item);
        return (
          <View key={item.id} style={styles.row}>
            <Text numberOfLines={2} style={styles.text}>
              · {item.text}
            </Text>
            {manageable && runNow ? (
              <Button
                label={t('queue.runNow')}
                accessibilityLabel={t('phone.queue.runNowLabel', { text: item.text.slice(0, 30) })}
                accessibilityHint={
                  runNow.kind === 'unavailable'
                    ? t(runNowReasonKey(runNow.reason))
                    : t(runNow.kind === 'worktree' ? 'phone.queue.runNowWorktreeHint' : 'queue.runNowHereHelp')
                }
                tone="neutral"
                compact
                dimmed={runNow.kind === 'unavailable'}
                busy={busy && runNow.kind !== 'unavailable'}
                onPress={() => (runNow.kind === 'unavailable' ? onExplain(t(runNowReasonKey(runNow.reason))) : onRunNow(item))}
              />
            ) : null}
            {manageable ? (
              <Button
                label={t('phone.queue.remove')}
                accessibilityLabel={t('phone.queue.removeLabel', { text: item.text.slice(0, 30) })}
                tone="ghost"
                compact
                busy={busy}
                onPress={() => onRemove(item.id)}
              />
            ) : null}
          </View>
        );
      })}
    </Animated.View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    wrap: {
      backgroundColor: palette.surface,
      borderRadius: radius.lg,
      gap: spacing.xs,
      padding: spacing.md,
    },
    header: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    title: { color: palette.textMuted, flex: 1, fontSize: 13, fontWeight: '600' },
    row: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    text: { ...monoText, color: palette.textFaint, flex: 1 },
  });
