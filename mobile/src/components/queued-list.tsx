import { useEffect, useRef } from 'react';
import { Animated, Easing, StyleSheet, Text, View } from 'react-native';
import type { QueuedItem } from '@/api/types';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { monoText, motion, radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/** How long a queued send's flash waits for its row to be listed. */
const FLASH_WAIT_MS = 5000;

/**
 * The pane's queue. Removing an item and starting the next one are offered only on a
 * host that advertised "queue"; otherwise the list reads exactly as it did before.
 */
export function QueuedList({
  items,
  manageable,
  canRunNext,
  busy,
  flash = 0,
  onRemove,
  onRunNext,
}: {
  items: QueuedItem[];
  manageable: boolean;
  /** The pane is idle, so the first item can be started by hand. */
  canRunNext: boolean;
  busy: boolean;
  /**
   * Bumped when a send from this phone was queued (natural submit B3): the list washes in
   * the run tint and fades back over `motion.highlight`. A colour change, not movement, so
   * Reduce Motion keeps it. 0 never flashes.
   */
  flash?: number;
  onRemove: (itemId: string) => void;
  onRunNext: () => void;
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
        {manageable && canRunNext ? (
          <Button label={t('queue.runNext')} tone="neutral" compact busy={busy} onPress={onRunNext} />
        ) : null}
      </View>
      {items.map((item) => (
        <View key={item.id} style={styles.row}>
          <Text numberOfLines={2} style={styles.text}>
            · {item.text}
          </Text>
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
      ))}
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
