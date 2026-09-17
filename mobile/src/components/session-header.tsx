import { StyleSheet, Text, View } from 'react-native';
import type { MobileSessionDetail } from '@/api/types';
import { StatusChip } from '@/components/status-chip';
import { colors, kindLabels, providerLabels, radius, spacing } from '@/theme';

function formatElapsed(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  if (minutes === 0) return `${rest}초`;
  return `${minutes}분 ${rest}초`;
}

export function SessionHeader({ detail }: { detail: MobileSessionDetail }) {
  const { session, usage, elapsedSeconds } = detail;
  const contextPercent =
    usage?.contextPercent ??
    (usage?.contextUsedTokens !== undefined && usage.contextWindowTokens
      ? (usage.contextUsedTokens / usage.contextWindowTokens) * 100
      : undefined);

  return (
    <View style={styles.header}>
      <View style={styles.titleRow}>
        <Text numberOfLines={2} style={styles.title}>
          {session.title || '제목 없음'}
        </Text>
        <StatusChip status={session.status} />
      </View>
      <View style={styles.metaRow}>
        <Text style={styles.meta}>{kindLabels[session.kind]}</Text>
        <Text style={styles.meta}>· {providerLabels[session.provider]}</Text>
        {(usage?.model ?? session.model) ? (
          <Text style={styles.meta}>· {usage?.model ?? session.model}</Text>
        ) : null}
        {elapsedSeconds !== undefined ? (
          <Text style={styles.meta}>· {formatElapsed(elapsedSeconds)}</Text>
        ) : null}
        {contextPercent !== undefined ? (
          <Text style={styles.meta}>· 컨텍스트 {contextPercent.toFixed(0)}%</Text>
        ) : null}
        {usage?.costUSD !== undefined ? (
          <Text style={styles.meta}>· ${usage.costUSD.toFixed(2)}</Text>
        ) : null}
      </View>
      {contextPercent !== undefined ? (
        <View style={styles.track}>
          <View
            style={[
              styles.fill,
              { width: `${Math.min(100, Math.max(0, contextPercent))}%` },
            ]}
          />
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  header: {
    borderBottomColor: colors.border,
    borderBottomWidth: StyleSheet.hairlineWidth,
    gap: spacing.xs,
    paddingBottom: spacing.md,
    marginBottom: spacing.sm,
  },
  titleRow: { alignItems: 'flex-start', flexDirection: 'row', gap: spacing.sm },
  title: { color: colors.text, flex: 1, fontSize: 16, fontWeight: '700' },
  metaRow: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
  meta: { color: colors.textFaint, fontSize: 12 },
  track: {
    backgroundColor: colors.surfaceRaised,
    borderRadius: radius.sm,
    height: 4,
    marginTop: spacing.xs,
    overflow: 'hidden',
  },
  fill: { backgroundColor: colors.accent, height: 4 },
});
