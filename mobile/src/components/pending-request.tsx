import { StyleSheet, Text, View } from 'react-native';
import { Pending } from '@/components/pending';
import { t } from '@/lib/i18n';
import { requestState, type PendingRequest } from '@/lib/pending-requests';
import { cardShadow, radius, spacing, typeScale, useStyles, type Palette } from '@/theme';

/**
 * A sent request the host has not shown yet (natural submit B1), drawn at the bottom of the
 * shown view: in the transcript the user's bubble with 전송 중 under it, in 흐름 and
 * 파이프라인 a 다음 요청 card. It shows the shared loading by the pending rule, and a failure's
 * message in place (요청 실패), where it stays until the next send.
 */
export function PendingRequestView({ request, look }: { request: PendingRequest; look: 'bubble' | 'card' }) {
  const styles = useStyles(makeStyles);
  const state = requestState(request);
  const failed = request.phase === 'failed';
  if (look === 'bubble') {
    return (
      <View style={styles.userWrap} testID="pending-request">
        {request.text ? (
          <View style={[styles.userBubble, failed && styles.failedBubble]}>
            <Text style={styles.userText}>{request.text}</Text>
          </View>
        ) : null}
        <Pending state={state} style={styles.mark} />
      </View>
    );
  }
  return (
    <View style={styles.card} testID="pending-request">
      <Text style={styles.cardLabel}>{t(failed ? 'graph.block.resultError' : 'graph.block.nextRequest')}</Text>
      {request.text ? (
        <Text numberOfLines={3} style={styles.cardText}>
          {request.text}
        </Text>
      ) : null}
      <Pending state={state} />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // The transcript's own user bubble (log-entry-view.tsx), with the mark under it.
    userWrap: { alignItems: 'flex-end', gap: spacing.sm, paddingLeft: spacing.xxl * 3, paddingVertical: spacing.xs },
    userBubble: {
      backgroundColor: palette.bubbleUser,
      borderBottomRightRadius: 6,
      borderRadius: 18,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md,
    },
    failedBubble: { opacity: 0.6 },
    userText: { color: palette.onBubbleUser, fontSize: 15, lineHeight: 21 },
    mark: { paddingHorizontal: spacing.xs },
    // A run card's look (mighty-blocks.tsx), with nothing run yet.
    card: {
      ...cardShadow,
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.card,
      gap: spacing.sm,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.lg,
    },
    cardLabel: { color: palette.textFaint, fontSize: 11, fontWeight: '700' },
    cardText: { ...typeScale.heading, color: palette.text, fontSize: 15, lineHeight: 21 },
  });
