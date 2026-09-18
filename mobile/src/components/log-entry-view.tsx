import { StyleSheet, Text, View } from 'react-native';
import type { LogActivity, LogEntry } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { monoText, radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/** Contract states plus the handful the host used before them; anything else is neutral. */
function activityColor(palette: Palette, state: string): string {
  switch (state) {
    case 'running':
      return palette.accent;
    case 'waiting':
    case 'pending':
      return palette.warning;
    case 'completed':
    case 'done':
      return palette.success;
    case 'error':
    case 'failed':
      return palette.danger;
    case 'stopped':
    case 'cancelled':
      return palette.grey;
    default:
      return palette.textMuted;
  }
}

/** `840ms`, `1.4초`, `2분 5초` — the tool's own wall-clock time. */
export function formatDuration(durationMs: number): string {
  if (!Number.isFinite(durationMs) || durationMs < 0) return '';
  if (durationMs < 1000) return `${Math.round(durationMs)}ms`;
  const seconds = durationMs / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)}초`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}분 ${Math.round(seconds % 60)}초`;
}

function ActivityRow({ activity }: { activity: LogActivity }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const color = activityColor(palette, activity.state);
  const duration = activity.durationMs !== undefined ? formatDuration(activity.durationMs) : '';
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
      {duration ? <Text style={styles.activityDuration}>{duration}</Text> : null}
      <Text style={[styles.activityState, { color }]}>{activity.state}</Text>
    </View>
  );
}

export function LogEntryView({ entry }: { entry: LogEntry }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);

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

  // `system`, `output`, `error` and any kind the contract adds later share this look.
  const tone =
    entry.kind === 'error'
      ? palette.danger
      : entry.kind === 'system'
        ? palette.textMuted
        : palette.text;

  return (
    <View style={styles.mono}>
      <Text style={[monoText, { color: tone }]}>{entry.text}</Text>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    userWrap: { alignItems: 'flex-end', paddingVertical: spacing.xs },
    userBubble: {
      backgroundColor: palette.bubbleUser,
      borderColor: palette.accentMuted,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      maxWidth: '85%',
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    userText: { color: palette.text, fontSize: 15, lineHeight: 21 },
    assistant: { paddingVertical: spacing.xs },
    mono: {
      backgroundColor: palette.surface,
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
    activityTitle: { color: palette.textMuted, fontSize: 13 },
    activityOutput: { ...monoText, color: palette.textFaint, marginTop: 2 },
    activityDuration: { color: palette.textFaint, fontSize: 11 },
    activityState: { fontSize: 11, fontWeight: '600' },
  });
