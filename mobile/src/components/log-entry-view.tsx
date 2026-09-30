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
    // The user's turn is a soft paper slip on the right; the reply is prose on the page.
    userWrap: { alignItems: 'flex-end', paddingVertical: spacing.sm },
    userBubble: {
      backgroundColor: palette.bubbleUser,
      borderRadius: radius.lg,
      maxWidth: '85%',
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm + 2,
    },
    userText: { color: palette.text, fontSize: 15, lineHeight: 22 },
    assistant: { paddingVertical: spacing.sm },
    // Output, system and error lines: mono, set off by a thin rule instead of a box.
    mono: {
      borderLeftColor: palette.border,
      borderLeftWidth: 2,
      marginVertical: spacing.xs,
      paddingLeft: spacing.sm,
      paddingVertical: 2,
    },
    // Tool activity whispers: faint text, a small dot, no chrome.
    activity: {
      alignItems: 'flex-start',
      flexDirection: 'row',
      gap: spacing.sm,
      paddingVertical: 3,
    },
    activityDot: { borderRadius: radius.round, height: 5, marginTop: 7, width: 5 },
    activityBody: { flex: 1 },
    activityTitle: { color: palette.textFaint, fontSize: 13, lineHeight: 18 },
    activityOutput: { ...monoText, color: palette.textFaint, marginTop: 2 },
    activityDuration: { color: palette.textFaint, fontSize: 11, lineHeight: 18 },
    activityState: { fontSize: 11, fontWeight: '500', lineHeight: 18 },
  });
