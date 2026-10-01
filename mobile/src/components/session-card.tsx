import { useEffect, useRef } from 'react';
import { Animated, Easing, Pressable, StyleSheet, Text, View } from 'react-native';
import { isAgentIOPane, type MobileSessionSummary } from '@/api/types';
import { Icon } from '@/components/icons';
import { ProviderMark } from '@/components/provider-mark';
import { BetaBadge } from '@/components/ui';
import { StatusChip } from '@/components/status-chip';
import {
  attentionOf,
  formatClock,
  freshGlance,
  relativeAge,
  sessionTone,
  type AgeUnit,
} from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { useNow } from '@/hooks/use-now';
import { useDetailReceivedAt, useSessionDetail } from '@/store/live';
import {
  headingFontFamily,
  kindLabel,
  monoText,
  providerColorsFor,
  providerIsBeta,
  providerLabel,
  radius,
  spacing,
  statusLabel,
  toneColors,
  typeScale,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

const AGE_KEYS: Record<AgeUnit, string> = {
  now: 'resume.time.now',
  minutes: 'resume.time.minutes',
  hours: 'resume.time.hours',
  days: 'resume.time.days',
};

/** `11분 전`, or nothing when the host sent a date that does not parse. */
export function ageText(updatedAt: string, now = Date.now()): string | undefined {
  const age = relativeAge(updatedAt, now);
  return age ? t(AGE_KEYS[age.unit], { count: age.count }) : undefined;
}

/**
 * The square at the head of a card: an agent pane wears its provider's mark in white on
 * the brand colour; a shell, browser or an agent's own pane wears a line glyph on ink.
 */
export function PaneAvatar({ session, size = 34 }: { session: MobileSessionSummary; size?: number }) {
  const palette = usePalette();
  const agent = session.kind === 'claude';
  const browser = session.kind === 'browser' || session.kind === 'agent-browser';
  const [brand = palette.idle] = providerColorsFor(palette, session.provider);
  return (
    <View
      style={{
        alignItems: 'center',
        backgroundColor: agent ? brand : palette.idle,
        borderRadius: Math.round(size / 3),
        height: size,
        justifyContent: 'center',
        width: size,
      }}
    >
      {agent ? (
        <ProviderMark provider={session.provider} size={Math.round(size * 0.52)} color={palette.onStatus} />
      ) : (
        <Icon
          name={browser ? 'globe' : 'terminal'}
          color={palette.onStatus}
          size={Math.round(size * 0.5)}
          strokeWidth={2.4}
        />
      )}
    </View>
  );
}

/** The dot that breathes outwards beside a running pane's last step. */
function PulseDot({ color }: { color: string }) {
  const spread = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    const animation = Animated.loop(
      Animated.timing(spread, {
        toValue: 1,
        duration: 1400,
        easing: Easing.out(Easing.quad),
        useNativeDriver: true,
      }),
    );
    animation.start();
    return () => animation.stop();
  }, [spread]);
  return (
    <View style={pulseStyles.box}>
      <Animated.View
        style={[
          pulseStyles.ring,
          {
            backgroundColor: color,
            opacity: spread.interpolate({ inputRange: [0, 1], outputRange: [0.5, 0] }),
            transform: [{ scale: spread.interpolate({ inputRange: [0, 1], outputRange: [1, 2.6] }) }],
          },
        ]}
      />
      <View style={[pulseStyles.dot, { backgroundColor: color }]} />
    </View>
  );
}

const pulseStyles = StyleSheet.create({
  box: { alignItems: 'center', height: 8, justifyContent: 'center', width: 8 },
  ring: { borderRadius: radius.round, height: 7, position: 'absolute', width: 7 },
  dot: { borderRadius: radius.round, height: 7, width: 7 },
});

/** An agent's pane, or a pane an agent owns, names its provider; a shell or browser does not. */
function showsProvider(session: MobileSessionSummary): boolean {
  return session.kind === 'claude' || isAgentIOPane(session.kind);
}

/** The pill on the right: what the pane waits on, or its own status. */
export function SessionPill({ session }: { session: MobileSessionSummary }) {
  if (session.pendingQuestions > 0) {
    return (
      <StatusChip status="waiting" label={t('phone.card.questions', { count: session.pendingQuestions })} />
    );
  }
  if (session.pendingPermissions > 0) {
    return (
      <StatusChip status="waiting" label={t('phone.card.permissions', { count: session.pendingPermissions })} />
    );
  }
  return <StatusChip status={session.status} />;
}

/**
 * One pane as a status card: a left edge in the pane's tone, the avatar square, the
 * title and meta, the status pill, and the pane's last step in mono. `glance` adds the
 * context gauge and the big elapsed time — only when the phone holds a session detail of
 * the same revision (`freshGlance`); the pane list itself carries neither number.
 */
export function SessionCard({
  hostId,
  session,
  glance = false,
  onPress,
}: {
  hostId: string;
  session: MobileSessionSummary;
  glance?: boolean;
  onPress: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const detail = useSessionDetail(glance ? hostId : undefined, session.id);
  const receivedAt = useDetailReceivedAt(glance ? hostId : undefined, session.id);
  const running = session.status === 'running';
  // A running card's clock moves every second; everyone else's "n분 전" every minute.
  const now = useNow(glance && running && detail !== undefined ? 1000 : 60_000);
  const numbers = glance ? freshGlance(session, detail, receivedAt, now) : undefined;
  const tone = sessionTone(session);
  const colors = toneColors(palette, tone);
  const loud = tone === 'run' || tone === 'wait';
  const title = session.title || t('phone.card.untitled');
  const age = running ? undefined : ageText(session.updatedAt, now);
  const attention = attentionOf(session);

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t('phone.card.label', {
        title,
        status: attention > 0 ? t('phone.card.attention', { count: attention }) : statusLabel(session.status),
      })}
      onPress={onPress}
      style={({ pressed }) => [
        styles.card,
        loud && { borderColor: colors.fill, borderWidth: 2 },
        pressed && styles.pressed,
      ]}
    >
      <View style={[styles.edge, { backgroundColor: tone === 'idle' ? palette.track : colors.fill }]} />
      <View style={styles.head}>
        <PaneAvatar session={session} />
        <View style={styles.titles}>
          <Text numberOfLines={1} style={styles.title}>
            {title}
          </Text>
          <View style={styles.metaRow}>
            <Text style={styles.meta}>{kindLabel(session.kind)}</Text>
            {showsProvider(session) ? (
              <>
                <Text style={styles.meta}>· {providerLabel(session.provider)}</Text>
                {providerIsBeta(session.provider) ? <BetaBadge /> : null}
              </>
            ) : null}
            {session.model ? <Text style={styles.meta}>· {session.model}</Text> : null}
            {session.terminal && !isAgentIOPane(session.kind) ? (
              <Text style={styles.terminalTag}>· {t('phone.card.localTerminal')}</Text>
            ) : null}
            {session.queued > 0 ? (
              <Text style={styles.meta}>· {t('phone.card.queued', { count: session.queued })}</Text>
            ) : null}
            {age ? <Text style={styles.meta}>· {age}</Text> : null}
          </View>
        </View>
        <SessionPill session={session} />
      </View>

      {numbers ? (
        <View style={styles.numbers}>
          {numbers.contextPercent !== undefined ? (
            <>
              <View
                accessible
                accessibilityLabel={t('phone.card.contextLabel', {
                  percent: Math.round(numbers.contextPercent),
                })}
                style={styles.track}
              >
                <View style={[styles.fill, { width: `${numbers.contextPercent}%` }]} />
              </View>
              <Text style={styles.trackLabel}>
                {t('phone.card.context', { percent: Math.round(numbers.contextPercent) })}
              </Text>
            </>
          ) : (
            <View style={styles.spacer} />
          )}
          {numbers.elapsedSeconds !== undefined ? (
            <Text
              accessibilityLabel={t('phone.card.elapsedLabel', { time: formatClock(numbers.elapsedSeconds) })}
              style={styles.elapsed}
            >
              {formatClock(numbers.elapsedSeconds)}
            </Text>
          ) : null}
        </View>
      ) : null}

      {session.preview?.text ? (
        <View style={styles.last}>
          {running ? <PulseDot color={palette.run} /> : null}
          <Text numberOfLines={2} style={styles.lastText}>
            {session.preview.text}
          </Text>
        </View>
      ) : null}
    </Pressable>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // Clipped so the tone edge follows the rounded corner; a clipped card casts no iOS
    // shadow, so the white on grey alone lifts it.
    card: {
      backgroundColor: palette.surface,
      borderColor: 'transparent',
      borderRadius: radius.card,
      borderWidth: 2,
      gap: spacing.sm + 2,
      overflow: 'hidden',
      paddingLeft: spacing.lg + 2,
      paddingRight: spacing.md,
      paddingVertical: spacing.md,
    },
    pressed: { opacity: 0.85, transform: [{ scale: 0.99 }] },
    edge: { bottom: 0, left: 0, position: 'absolute', top: 0, width: 5 },
    head: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm + 2 },
    titles: { flex: 1, gap: 2 },
    title: { color: palette.text, fontSize: 15.5, fontWeight: '700', lineHeight: 20 },
    metaRow: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: 4 },
    meta: { color: palette.textMuted, fontSize: 12 },
    terminalTag: { color: palette.warning, fontSize: 12, fontWeight: '600' },
    numbers: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm + 2 },
    track: {
      backgroundColor: palette.runSoft,
      borderRadius: 4,
      flex: 1,
      height: 8,
      overflow: 'hidden',
    },
    fill: { backgroundColor: palette.run, borderRadius: 4, height: 8 },
    trackLabel: { color: palette.textMuted, fontSize: 11.5, fontWeight: '600' },
    spacer: { flex: 1 },
    elapsed: {
      ...typeScale.title,
      color: palette.text,
      fontFamily: headingFontFamily,
      fontSize: 22,
      fontVariant: ['tabular-nums'],
      lineHeight: 26,
    },
    last: {
      alignItems: 'center',
      backgroundColor: palette.surfaceRaised,
      borderRadius: 10,
      flexDirection: 'row',
      gap: 7,
      paddingHorizontal: 10,
      paddingVertical: 7,
    },
    lastText: { ...monoText, color: palette.textMuted, flex: 1, fontSize: 11.5, lineHeight: 16 },
  });
