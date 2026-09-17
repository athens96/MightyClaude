import { StyleSheet, Text, View } from 'react-native';
import type { LogActivity, LogEntry } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { colors, monoText, radius, spacing } from '@/theme';

const activityStateColors: Record<string, string> = {
  running: colors.accent,
  pending: colors.textMuted,
  done: colors.success,
  completed: colors.success,
  error: colors.danger,
  failed: colors.danger,
  cancelled: colors.grey,
};

function ActivityRow({ activity }: { activity: LogActivity }) {
  const color = activityStateColors[activity.state] ?? colors.textMuted;
  return (
    <View style={styles.activity}>
      <View style={[styles.activityDot, { backgroundColor: color }]} />
      <View style={styles.activityBody}>
        <Text numberOfLines={1} style={styles.activityTitle}>
          {activity.toolName ? `${activity.toolName} · ` : ''}
          {activity.summary || activity.kind}
        </Text>
        {activity.output ? (
          <Text numberOfLines={6} style={styles.activityOutput}>
            {activity.output}
          </Text>
        ) : null}
      </View>
      <Text style={[styles.activityState, { color }]}>{activity.state}</Text>
    </View>
  );
}

export function LogEntryView({ entry }: { entry: LogEntry }) {
  if (entry.activity) {
    return <ActivityRow activity={entry.activity} />;
  }

  if (entry.kind === 'user') {
    return (
      <View style={styles.userWrap}>
        <View style={styles.userBubble}>
          <Text style={styles.userText}>{entry.text}</Text>
        </View>
      </View>
    );
  }

  if (entry.kind === 'assistant') {
    return (
      <View style={styles.assistant}>
        <AssistantMarkdown text={entry.text} />
      </View>
    );
  }

  const tone =
    entry.kind === 'error' ? colors.danger : entry.kind === 'system' ? colors.textMuted : colors.text;

  return (
    <View style={styles.mono}>
      <Text style={[monoText, { color: tone }]}>{entry.text}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  userWrap: { alignItems: 'flex-end', paddingVertical: spacing.xs },
  userBubble: {
    backgroundColor: colors.bubbleUser,
    borderColor: colors.accentMuted,
    borderRadius: radius.lg,
    borderWidth: StyleSheet.hairlineWidth,
    maxWidth: '85%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
  },
  userText: { color: colors.text, fontSize: 15, lineHeight: 21 },
  assistant: { paddingVertical: spacing.xs },
  mono: {
    backgroundColor: colors.surface,
    borderRadius: radius.sm,
    marginVertical: spacing.xs,
    padding: spacing.sm,
  },
  activity: {
    alignItems: 'flex-start',
    flexDirection: 'row',
    gap: spacing.sm,
    paddingVertical: spacing.xs,
  },
  activityDot: { borderRadius: 3, height: 6, marginTop: 6, width: 6 },
  activityBody: { flex: 1 },
  activityTitle: { color: colors.textMuted, fontSize: 13 },
  activityOutput: { ...monoText, color: colors.textFaint, marginTop: 2 },
  activityState: { fontSize: 11, fontWeight: '600' },
});
