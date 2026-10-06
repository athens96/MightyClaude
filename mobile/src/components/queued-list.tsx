import { StyleSheet, Text, View } from 'react-native';
import type { QueuedItem } from '@/api/types';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { monoText, radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * The pane's queue. Removing an item and starting the next one are offered only on a
 * host that advertised "queue"; otherwise the list reads exactly as it did before.
 */
export function QueuedList({
  items,
  manageable,
  canRunNext,
  busy,
  onRemove,
  onRunNext,
}: {
  items: QueuedItem[];
  manageable: boolean;
  /** The pane is idle, so the first item can be started by hand. */
  canRunNext: boolean;
  busy: boolean;
  onRemove: (itemId: string) => void;
  onRunNext: () => void;
}) {
  const styles = useStyles(makeStyles);
  if (items.length === 0) return null;
  return (
    <View style={styles.wrap}>
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
    </View>
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
