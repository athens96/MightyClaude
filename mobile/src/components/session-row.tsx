import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { MobileSessionSummary } from '@/api/types';
import { Badge } from '@/components/ui';
import { StatusChip } from '@/components/status-chip';
import { colors, kindLabels, monoText, providerLabels, radius, spacing } from '@/theme';

export function SessionRow({
  session,
  onPress,
}: {
  session: MobileSessionSummary;
  onPress: () => void;
}) {
  const attention = session.pendingPermissions + session.pendingQuestions;
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [styles.row, pressed && styles.pressed]}
    >
      <View style={styles.header}>
        <Text numberOfLines={1} style={styles.title}>
          {session.title || '제목 없음'}
        </Text>
        <Badge count={attention} />
        <StatusChip status={session.status} />
      </View>

      <View style={styles.metaRow}>
        <Text style={styles.meta}>{kindLabels[session.kind]}</Text>
        <Text style={styles.meta}>· {providerLabels[session.provider]}</Text>
        {session.model ? <Text style={styles.meta}>· {session.model}</Text> : null}
        {session.terminal ? <Text style={styles.terminalTag}>· 로컬 터미널</Text> : null}
        {session.queued > 0 ? <Text style={styles.meta}>· 대기 {session.queued}</Text> : null}
      </View>

      {session.preview ? (
        <Text numberOfLines={2} style={styles.preview}>
          {session.preview.text}
        </Text>
      ) : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  row: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: radius.md,
    borderWidth: StyleSheet.hairlineWidth,
    gap: spacing.xs,
    padding: spacing.md,
  },
  pressed: { opacity: 0.7 },
  header: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  title: { color: colors.text, flex: 1, fontSize: 15, fontWeight: '600' },
  metaRow: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
  meta: { color: colors.textFaint, fontSize: 12 },
  terminalTag: { color: colors.warning, fontSize: 12 },
  preview: { ...monoText, color: colors.textMuted },
});
