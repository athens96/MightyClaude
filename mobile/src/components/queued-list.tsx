import { StyleSheet, Text, View } from 'react-native';
import type { QueuedItem } from '@/api/types';
import { Button } from '@/components/ui';
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
        <Text style={styles.title}>대기열 {items.length}건</Text>
        {manageable && canRunNext ? (
          <Button label="다음 실행" tone="neutral" compact busy={busy} onPress={onRunNext} />
        ) : null}
      </View>
      {items.map((item) => (
        <View key={item.id} style={styles.row}>
          <Text numberOfLines={2} style={styles.text}>
            · {item.text}
          </Text>
          {manageable ? (
            <Button
              label="삭제"
              accessibilityLabel={`${item.text.slice(0, 30)} 대기열에서 삭제`}
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
      borderRadius: radius.sm,
      gap: spacing.xs,
      padding: spacing.md,
    },
    header: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    title: { color: palette.textMuted, flex: 1, fontSize: 13, fontWeight: '700' },
    row: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    text: { ...monoText, color: palette.textFaint, flex: 1 },
  });
