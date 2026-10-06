import { useState } from 'react';
import { ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import type { MobilePermission, PlanDecisionKind } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { canSendRevise, feedbackTooLong, receivedText } from '@/lib/plan';
import { cardShadow, radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The phone's copy of the Mac's plan card: Claude's plan (ExitPlanMode) as Markdown in a
 * body that scrolls, then the four answers — 승인하고 실행 (자동 편집) on the blue, 승인하고
 * 실행 (매번 확인), 수정 요청 (which opens a text box and sends only once there are words) and
 * 취소. The screen keys it by run and request id, so a new plan starts with an empty box.
 */
export function PlanCard({
  permission,
  busy,
  maxBodyHeight,
  onAnswer,
}: {
  permission: MobilePermission;
  busy: boolean;
  /** The ceiling of the scrolling plan, so the card never walks the transcript off screen. */
  maxBodyHeight: number;
  onAnswer: (decision: PlanDecisionKind, feedback?: string) => void;
}) {
  const styles = useStyles(makeStyles);
  const palette = usePalette();
  const [revising, setRevising] = useState(false);
  const [feedback, setFeedback] = useState('');
  const received = receivedText(permission.receivedAt);

  return (
    <View style={styles.card} testID={`plan-${permission.id}`}>
      <View style={styles.header}>
        <Text style={styles.badge}>{t('plan.card.title')}</Text>
        {received ? <Text style={styles.received}>{received}</Text> : null}
      </View>
      <ScrollView style={{ maxHeight: maxBodyHeight }} nestedScrollEnabled testID="plan-text">
        <AssistantMarkdown text={permission.plan ?? ''} selectable />
      </ScrollView>

      {revising ? (
        <View style={styles.revise}>
          <TextInput
            testID="plan-revise-text"
            style={styles.input}
            multiline
            editable={!busy}
            value={feedback}
            onChangeText={setFeedback}
            placeholder={t('plan.card.revisePlaceholder')}
            placeholderTextColor={palette.textFaint}
            accessibilityLabel={t('plan.card.revisePlaceholder')}
          />
          {feedbackTooLong(feedback) ? (
            <Text style={styles.error} testID="plan-revise-too-long">
              {t('plan.error.feedbackTooLong')}
            </Text>
          ) : null}
          <View style={styles.row}>
            <Button
              label={t('plan.card.reviseClose')}
              tone="neutral"
              compact
              disabled={busy}
              style={styles.action}
              onPress={() => setRevising(false)}
            />
            <Button
              label={t('plan.card.reviseSend')}
              tone="ink"
              compact
              busy={busy}
              disabled={!canSendRevise(feedback)}
              style={styles.action}
              onPress={() => onAnswer('revise', feedback)}
            />
          </View>
        </View>
      ) : null}

      <Text style={styles.hint}>{t('plan.card.hint')}</Text>
      <View style={styles.row}>
        <Button label={t('plan.card.cancel')} tone="neutral" compact disabled={busy} style={styles.action} onPress={() => onAnswer('cancel')} />
        <Button
          label={t('plan.card.revise')}
          tone="neutral"
          compact
          disabled={busy || revising}
          style={styles.action}
          onPress={() => setRevising(true)}
        />
      </View>
      <View style={styles.row}>
        <Button
          label={t('plan.card.approveConfirm')}
          tone="neutral"
          compact
          disabled={busy}
          style={styles.action}
          onPress={() => onAnswer('approveConfirmEach')}
        />
        <Button
          label={t('plan.card.approveAuto')}
          tone="primary"
          compact
          busy={busy}
          style={styles.action}
          onPress={() => onAnswer('approveAutoEdit')}
        />
      </View>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // The same amber-ringed white card a question docks in.
    card: {
      ...cardShadow,
      backgroundColor: palette.surface,
      borderColor: palette.wait,
      borderRadius: radius.lg,
      borderWidth: 2,
      gap: spacing.sm,
      marginHorizontal: spacing.md,
      marginTop: spacing.sm,
      padding: spacing.md,
    },
    header: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    badge: {
      backgroundColor: palette.wait,
      borderRadius: radius.round,
      color: palette.onWait,
      fontSize: 11.5,
      fontWeight: '800',
      overflow: 'hidden',
      paddingHorizontal: 9,
      paddingVertical: 3,
    },
    received: { color: palette.textMuted, fontSize: 12 },
    revise: { gap: spacing.sm },
    input: {
      ...typeScale.body,
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: 1,
      color: palette.text,
      maxHeight: 120,
      minHeight: 56,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
      textAlignVertical: 'top',
    },
    hint: { color: palette.textMuted, fontSize: 12 },
    error: { color: palette.danger, fontSize: 12 },
    row: { flexDirection: 'row', gap: spacing.sm },
    action: { flex: 1 },
  });
