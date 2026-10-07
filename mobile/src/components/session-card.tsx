import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { MobileSessionSummary } from '@/api/types';
import { ProviderMark } from '@/components/provider-mark';
import { BetaBadge } from '@/components/ui';
import { StatusGlyph } from '@/components/status-glyph';
import {
  attentionOf,
  displayStatus,
  freshGlance,
  relativeAge,
  sessionTone,
  type AgeUnit,
} from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { rowMark, rowMeta, rowNote, waitLabel } from '@/lib/session-row';
import { statusWord } from '@/lib/status-glyph';
import { useNow } from '@/hooks/use-now';
import { useDetailReceivedAt, useSessionDetail } from '@/store/live';
import { monoText, spacing, useStyles, type Palette } from '@/theme';

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

/** The status glyph's width plus the gap after it: where the title, and the rule, begin. */
export const ROW_GLYPH = 18;
export const ROW_GAP = spacing.lg;
export const ROW_PADDING = spacing.lg;
export const ROW_TEXT_INSET = ROW_PADDING + ROW_GLYPH + ROW_GAP;
/** The provider's mark before its name, sized to the 12.5pt meta line. */
const MARK_SIZE = 12;

/**
 * One pane as a row in its workspace's list (concept A, "글리프 행"): the status glyph,
 * the title, and one muted line — what runs it, an agent's pane with its provider's brand
 * mark before the name (`rowMark`; decorative, as the name says it), then the elapsed time
 * and context while it runs or waits, or how long ago it settled (`rowMeta`). A running
 * pane adds its last step in mono, a pane stopped on an error its reason in the error ink
 * (`rowNote`); a pane waiting on the user ends in an amber `질문 1`. `glance` lets the
 * row show elapsed and context — only when the phone holds a session detail of the same
 * revision (`freshGlance`); the pane list itself carries neither number. Rows after the
 * first draw a hairline rule from the title's edge.
 */
export function SessionRow({
  hostId,
  session,
  glance = false,
  first = false,
  onPress,
}: {
  hostId: string;
  session: MobileSessionSummary;
  glance?: boolean;
  first?: boolean;
  onPress: () => void;
}) {
  const styles = useStyles(makeStyles);
  const detail = useSessionDetail(glance ? hostId : undefined, session.id);
  const receivedAt = useDetailReceivedAt(glance ? hostId : undefined, session.id);
  const running = session.status === 'running';
  // A running row's clock moves every second; everyone else's "n분 전" every minute.
  const now = useNow(glance && running && detail !== undefined ? 1000 : 60_000);
  const numbers = glance ? freshGlance(session, detail, receivedAt, now) : undefined;
  const status = displayStatus(session);
  const tone = sessionTone(session);
  const settled = tone === 'done' || tone === 'stop' || tone === 'idle';
  const title = session.title || t('phone.card.untitled');
  const age = running ? undefined : ageText(session.updatedAt, now);
  const attention = attentionOf(session);
  const ask = waitLabel(session);
  const { lead, beta, rest } = rowMeta(session, numbers, age);
  const mark = rowMark(session);
  const note = rowNote(session);
  const word = attention > 0 ? t('phone.card.attention', { count: attention }) : statusWord(status);

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t('phone.card.label', {
        title,
        status: note?.kind === 'reason' ? `${word}, ${note.text}` : word,
      })}
      onPress={onPress}
      style={({ pressed }) => [styles.row, pressed && styles.pressed]}
    >
      {first ? null : <View style={styles.rule} />}
      <View style={styles.glyph}>
        <StatusGlyph status={status} kind={session.kind} size={ROW_GLYPH} decorative />
      </View>
      <View style={styles.texts}>
        <Text numberOfLines={1} style={[styles.title, settled && styles.titleSettled]}>
          {title}
        </Text>
        {beta || mark ? (
          <View style={styles.metaRow}>
            {mark ? <ProviderMark provider={mark} size={MARK_SIZE} /> : null}
            <Text numberOfLines={1} style={styles.meta}>
              {lead.join(' · ')}
            </Text>
            {beta ? <BetaBadge /> : null}
            {rest.length > 0 ? (
              <Text numberOfLines={1} style={[styles.meta, styles.metaRest]}>
                · {rest.join(' · ')}
              </Text>
            ) : null}
          </View>
        ) : (
          <Text numberOfLines={1} style={[styles.meta, styles.metaLine]}>
            {[...lead, ...rest].join(' · ')}
          </Text>
        )}
        {note ? (
          <Text numberOfLines={1} style={[styles.note, note.kind === 'reason' && styles.reason]}>
            {note.text}
          </Text>
        ) : null}
      </View>
      {ask ? <Text style={styles.ask}>{ask}</Text> : null}
    </Pressable>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    row: {
      alignItems: 'flex-start',
      flexDirection: 'row',
      gap: ROW_GAP,
      paddingHorizontal: ROW_PADDING,
      paddingVertical: spacing.md,
    },
    pressed: { backgroundColor: palette.surfaceRaised },
    rule: {
      backgroundColor: palette.border,
      height: 1,
      left: ROW_TEXT_INSET,
      position: 'absolute',
      right: 0,
      top: 0,
    },
    glyph: { marginTop: 1 },
    texts: { flex: 1, minWidth: 0 },
    title: { color: palette.text, fontSize: 15, fontWeight: '600', lineHeight: 20 },
    titleSettled: { fontWeight: '500' },
    metaRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm, marginTop: 1 },
    meta: { color: palette.textMuted, fontSize: 12.5, lineHeight: 17 },
    metaLine: { marginTop: 1 },
    metaRest: { flexShrink: 1 },
    note: { ...monoText, color: palette.textMuted, fontSize: 11.3, lineHeight: 16, marginTop: spacing.sm },
    reason: { color: palette.danger },
    ask: { color: palette.warning, fontSize: 12.5, fontWeight: '700', lineHeight: 20 },
  });
