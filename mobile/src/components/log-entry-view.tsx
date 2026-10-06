import { StyleSheet, Text, View } from 'react-native';
import type { LogActivity, LogEntry } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { ProviderMark } from '@/components/provider-mark';
import { t } from '@/lib/i18n';
import type { Tone } from '@/lib/status-tone';
import {
  cardShadow,
  monoText,
  providerColorsFor,
  providerLabel,
  radius,
  spacing,
  toneColors,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/** Contract states plus the handful the host used before them; anything else is neutral. */
function activityTone(state: string): Tone {
  switch (state) {
    case 'running':
      return 'run';
    case 'waiting':
    case 'pending':
      return 'wait';
    case 'completed':
    case 'done':
      return 'done';
    case 'error':
    case 'failed':
      return 'err';
    case 'stopped':
    case 'cancelled':
      return 'stop';
    default:
      return 'idle';
  }
}

/** The glyph inside the coloured square: ✓ done, ✕ failed, … still going. */
function activityGlyph(tone: Tone): string {
  switch (tone) {
    case 'done':
      return '✓';
    case 'err':
      return '✕';
    case 'stop':
      return '■';
    case 'wait':
      return '?';
    case 'run':
      return '…';
    case 'idle':
      return '·';
  }
}

/** `840ms`, `1.4s`, `2m 5s` (in the app's language) — the tool's own wall-clock time. */
export function formatDuration(durationMs: number): string {
  if (!Number.isFinite(durationMs) || durationMs < 0) return '';
  if (durationMs < 1000) return `${Math.round(durationMs)}ms`;
  const seconds = durationMs / 1000;
  if (seconds < 60) return t('run.activity.durationSeconds', { seconds: seconds.toFixed(1) });
  const minutes = Math.floor(seconds / 60);
  return t('run.activity.durationMinutesSeconds', { minutes, seconds: Math.round(seconds % 60) });
}

/**
 * A tool call as a compact chip: a square in the call's state colour with ✓ / ✕ in it,
 * the tool's name in bold, what it did in mono, then its time and state word. A failed
 * call is ringed in red. The log sends calls one entry at a time, so each is its own row
 * rather than a two-column strip.
 */
function ActivityRow({ activity }: { activity: LogActivity }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const tone = activityTone(activity.state);
  const colors = toneColors(palette, tone);
  const duration = activity.durationMs !== undefined ? formatDuration(activity.durationMs) : '';
  const name = activity.toolName || activity.kind;
  const detail = activity.toolName ? activity.summary : activity.summary === activity.kind ? '' : activity.summary;
  return (
    <View style={[styles.activity, tone === 'err' && { borderColor: colors.fill }]}>
      <View style={[styles.glyph, { backgroundColor: colors.fill }]}>
        <Text style={[styles.glyphMark, { color: colors.onFill }]}>{activityGlyph(tone)}</Text>
      </View>
      <View style={styles.activityBody}>
        <View style={styles.activityHead}>
          <Text numberOfLines={1} style={styles.activityTitle}>
            {name}
          </Text>
          {duration ? <Text style={styles.activityDuration}>{duration}</Text> : null}
          <Text style={[styles.activityState, { color: colors.ink }]}>{activity.state}</Text>
        </View>
        {detail ? (
          <Text numberOfLines={1} style={styles.activityDetail}>
            {detail}
          </Text>
        ) : null}
        {activity.output ? (
          <Text numberOfLines={6} style={styles.activityOutput}>
            {activity.output}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

/** Who is speaking, above a reply card: the provider's mark on its square and its name. */
function Speaker({ provider }: { provider: string }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [brand = palette.idle] = providerColorsFor(palette, provider);
  return (
    <View style={styles.speaker}>
      <View style={[styles.speakerMark, { backgroundColor: brand }]}>
        <ProviderMark provider={provider} size={12} color={palette.onStatus} />
      </View>
      <Text style={styles.speakerName}>{providerLabel(provider)}</Text>
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
        {entry.provider ? <Speaker provider={entry.provider} /> : null}
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
    // The user's turn is an ink bubble on the right, its corner tucked toward the edge;
    // the reply is a white card with its speaker on top.
    userWrap: { alignItems: 'flex-end', paddingLeft: 48, paddingVertical: spacing.xs },
    userBubble: {
      backgroundColor: palette.bubbleUser,
      borderBottomRightRadius: 6,
      borderRadius: 18,
      paddingHorizontal: 14,
      paddingVertical: 10,
    },
    userText: { color: palette.onBubbleUser, fontSize: 15, lineHeight: 21 },
    assistant: {
      ...cardShadow,
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      marginVertical: spacing.xs,
      paddingBottom: spacing.xs,
      paddingHorizontal: spacing.md + 2,
      paddingTop: spacing.md,
    },
    speaker: { alignItems: 'center', flexDirection: 'row', gap: 6, marginBottom: spacing.xs },
    speakerMark: {
      alignItems: 'center',
      borderRadius: 6,
      height: 20,
      justifyContent: 'center',
      width: 20,
    },
    speakerName: { color: palette.textMuted, fontSize: 12, fontWeight: '700' },
    // Output, system and error lines: mono, set off by a coloured rule instead of a box.
    mono: {
      borderLeftColor: palette.border,
      borderLeftWidth: 3,
      borderRadius: 2,
      marginVertical: spacing.xs,
      paddingLeft: spacing.sm,
      paddingVertical: 2,
    },
    activity: {
      alignItems: 'flex-start',
      backgroundColor: palette.surface,
      borderColor: 'transparent',
      borderRadius: radius.md,
      borderWidth: 1.5,
      flexDirection: 'row',
      gap: 8,
      marginVertical: 3,
      paddingHorizontal: 9,
      paddingVertical: 7,
    },
    glyph: {
      alignItems: 'center',
      borderRadius: 6,
      height: 18,
      justifyContent: 'center',
      marginTop: 1,
      width: 18,
    },
    glyphMark: { fontSize: 11, fontWeight: '800', lineHeight: 13 },
    activityBody: { flex: 1, gap: 1 },
    activityHead: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    activityTitle: { color: palette.text, flex: 1, fontSize: 12.5, fontWeight: '700' },
    activityDetail: { ...monoText, color: palette.textMuted, fontSize: 11, lineHeight: 15 },
    activityOutput: { ...monoText, color: palette.textMuted, fontSize: 11, lineHeight: 15, marginTop: 2 },
    activityDuration: { color: palette.textFaint, fontSize: 11 },
    activityState: { fontSize: 11, fontWeight: '700' },
  });
