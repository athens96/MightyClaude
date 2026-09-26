import { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
} from 'react-native';
import type { StyleAction, StylePanel } from '@/api/types';
import { GuidedActionChip } from '@/components/guided-action-chip';
import { ActionListSheet, InfoSheet } from '@/components/sheets';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { styleViewModel, type StyleViewModel } from '@/lib/styles';
import { monoText, radius, spacing, tintColor, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The composer's guided chrome, for whatever style the pane is running. The host owns
 * everything on screen — the phase, the groups, the catalogue, what comes next — and the
 * phone only draws it and posts the chosen action to `/guided`. There is no closed set
 * of styles here: a panel arrives, it is drawn, and the source badge next to the name
 * says when the name is not one the app shipped (contract 1.10).
 *
 * Manifest strings are data: they are drawn as plain text, never markdown, and the setup
 * block shows its install command without any way to run or pre-fill it (contract 4.6).
 */
export function GuidedPanel({
  panel,
  hasText,
  busyActionId,
  disabled,
  running,
  selectedGroupId,
  onSelectGroup,
  onRun,
}: {
  panel: StylePanel;
  /** Whether the composer has something the text-taking actions would carry. */
  hasText: boolean;
  /** The action currently in flight, if any. */
  busyActionId?: string;
  disabled: boolean;
  /** Whether the pane itself is working; a style may send no next step while it is. */
  running: boolean;
  /** The group of the map on screen; held by the screen so it survives a question card. */
  selectedGroupId?: string;
  onSelectGroup: (groupId: string) => void;
  onRun: (actionId: string) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { height } = useWindowDimensions();
  const [moreOpen, setMoreOpen] = useState(false);
  const [detail, setDetail] = useState<StyleAction | undefined>(undefined);

  // A pane that switches style keeps this component mounted, so a sheet opened against
  // the old style's catalogue has to go with it.
  const styleId = panel.style.id;
  useEffect(() => {
    setMoreOpen(false);
    setDetail(undefined);
  }, [styleId]);

  const model = useMemo(() => styleViewModel(panel, selectedGroupId), [panel, selectedGroupId]);
  const tint = tintColor(palette, model.tint);
  // Nothing can run until the Mac reports the style ready, so the chips say so by being
  // unpressable rather than by the host answering 409 a moment later.
  const locked = disabled || model.setup !== undefined;
  const lines = bottomLines(model, hasText);
  // A style may hold 100 actions and 16 groups; without a ceiling the panel walks the
  // composer and the transcript off the bottom of the screen with no way to scroll back.
  const bodyMaxHeight = Math.round(height * 0.4);

  return (
    <View style={styles.panel}>
      <View style={styles.headRow}>
        <Text numberOfLines={1} style={[styles.headTitle, { color: tint }]}>
          {model.headerTitle}
        </Text>
        {model.sourceBadge ? (
          <Text style={styles.sourceBadge}>{model.sourceBadge}</Text>
        ) : null}
      </View>

      <ScrollView
        contentContainerStyle={styles.body}
        keyboardShouldPersistTaps="handled"
        style={{ maxHeight: bodyMaxHeight }}
      >
        {/* Done steps are the tint dimmed, the current one is the tint at full height, and
            the rest are the border colour. The counter is one string that never shrinks:
            split across text fragments, Android measured it a glyph short. */}
        {model.phase && model.phase.count > 1 ? (
          <View
            accessible
            accessibilityLabel={t('phone.guided.phaseProgress', {
              title: model.phase.title,
              current: model.phase.index + 1,
              count: model.phase.count,
            })}
            style={styles.stepper}
          >
            {Array.from({ length: model.phase.count }, (_, index) => {
              const current = model.phase?.index ?? 0;
              return (
                <View
                  key={index}
                  style={[
                    styles.step,
                    index < current && [styles.stepDone, { backgroundColor: tint }],
                    index === current && [styles.stepCurrent, { backgroundColor: tint }],
                  ]}
                />
              );
            })}
            <Text style={[styles.stepCount, { borderColor: tint, color: tint }]}>
              {`${model.phase.index + 1}/${model.phase.count}`}
            </Text>
          </View>
        ) : null}

        {model.setup ? (
          <View style={styles.setup}>
            {model.setup.missing.map((line, index) => (
              <Text key={index} style={styles.warning}>
                {line}
              </Text>
            ))}
            {model.setup.hint ? <Text style={styles.hint}>{model.setup.hint}</Text> : null}
            {model.setup.installCommand ? (
              <>
                <Text selectable style={styles.command}>
                  {model.setup.installCommand}
                </Text>
                <Text style={styles.hint}>이 명령은 Mac에서 직접 실행하세요.</Text>
              </>
            ) : null}
          </View>
        ) : null}

        {model.showMap ? (
          <View style={styles.map}>
            {model.groups.map((group) => {
              const selected = group.id === model.selectedGroupId;
              return (
                <Pressable
                  accessibilityRole="button"
                  accessibilityLabel={group.axis ? `${group.title} · ${group.axis}` : group.title}
                  accessibilityState={{ selected }}
                  key={group.id}
                  onPress={() => onSelectGroup(group.id)}
                  style={({ pressed }) => [
                    styles.cell,
                    selected && { borderColor: tint, backgroundColor: palette.accentMuted },
                    pressed && styles.pressed,
                  ]}
                >
                  <Text style={[styles.cellTitle, selected && { color: tint }]}>{group.title}</Text>
                  {group.axis ? <Text style={styles.cellAxis}>{group.axis}</Text> : null}
                </Pressable>
              );
            })}
          </View>
        ) : null}

        {model.question ? <Text style={styles.question}>{model.question}</Text> : null}

        {model.attachments.length > 0 ? (
          <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.attachments}>
            <View style={styles.attachmentRow}>
              {model.attachments.map((attachment, index) => (
                // Two attachments can carry the same name under different folders.
                <View key={index} style={styles.attachmentItem}>
                  {attachment.detail !== model.attachments[index - 1]?.detail ? (
                    <Text style={styles.attachmentDetail}>{attachment.detail}</Text>
                  ) : null}
                  <Text style={styles.attachmentTitle}>{attachment.title}</Text>
                  {/* The same mark a read-only action chip wears; the phone cannot open
                      the file either way, and the host says so per attachment (7.3). */}
                  {attachment.readOnly ? (
                    <Text accessibilityLabel="읽기 전용" style={styles.attachmentMark}>
                      👁
                    </Text>
                  ) : null}
                </View>
              ))}
            </View>
          </ScrollView>
        ) : null}

        {/* A style can leave `next` empty — while the pane is working, or on a phase it
            asks about rather than offers steps for. The catalogue is still there, so the
            only thing that changes is which chips are drawn, never whether it is reachable. */}
        {model.actions.length === 0 ? (
          running ? (
            <View style={styles.busyRow}>
              <ActivityIndicator color={tint} size="small" />
              {/* A text beside a spinner only wraps once it may shrink; otherwise the row
                  runs off the panel and the sentence is cut where the screen ends. */}
              <Text lineBreakStrategyIOS="hangul-word" numberOfLines={3} style={[styles.hint, styles.busyText]}>
                실행 중입니다. 끝나면 다음 행동이 나옵니다.
              </Text>
            </View>
          ) : (
            <Text style={styles.hint}>지금은 고를 행동이 없습니다. 아래에 적어 그대로 보내세요.</Text>
          )
        ) : null}

        {model.actions.length > 0 || model.rest.length > 0 ? (
          <View style={styles.actions}>
            {model.actions.map(({ action, prominent, recommended }) => (
              <GuidedActionChip
                key={action.id}
                action={action}
                prominent={prominent}
                recommended={recommended}
                tint={tint}
                busy={busyActionId === action.id}
                disabled={
                  locked ||
                  (action.requiresText && !hasText) ||
                  (busyActionId !== undefined && busyActionId !== action.id)
                }
                onPress={() => onRun(action.id)}
                onLongPress={() => setDetail(action)}
              />
            ))}
            {model.rest.length > 0 ? (
              <Button
                label="더 보기"
                tone="ghost"
                compact
                disabled={locked || busyActionId !== undefined}
                onPress={() => setMoreOpen(true)}
              />
            ) : null}
          </View>
        ) : null}

        {lines.map((line, index) => (
          <Text key={index} style={styles.hint}>
            {line}
          </Text>
        ))}
      </ScrollView>

      <ActionListSheet
        visible={moreOpen}
        title={`${panel.style.name} 행동`}
        note="호스트가 이 실행 창에서 쓸 수 있다고 알려 준 행동입니다."
        actions={model.rest.map((action) => ({
          id: action.id,
          label: action.glyph ? `${action.glyph} ${action.title}` : action.title,
          description: action.help,
        }))}
        busy={locked || busyActionId !== undefined}
        onSelect={(actionId) => {
          setMoreOpen(false);
          onRun(actionId);
        }}
        onClose={() => setMoreOpen(false)}
      />

      <InfoSheet
        visible={detail !== undefined}
        title={detail ? `${detail.glyph ?? ''} ${detail.title}`.trim() : ''}
        lines={detail ? actionLines(detail) : []}
        onClose={() => setDetail(undefined)}
      />
    </View>
  );
}

/**
 * The panel's closing lines, in order: the manifest's own guidance, then which of the
 * actions on screen would carry what is in the composer, then the app's own note that a
 * chip has more to say. They answer different questions — a style that writes guidance
 * does not thereby stop taking the composer text — so none of them hides another, and
 * the long-press hint is chrome this app owns rather than anything a manifest wrote.
 */
function bottomLines(model: StyleViewModel, hasText: boolean): string[] {
  const lines: string[] = [];
  if (model.guidance) lines.push(model.guidance);
  if (model.takesText.length > 0) {
    const names = model.takesText.map((action) => action.title).join(', ');
    lines.push(
      hasText
        ? `${names}은(는) 입력창의 내용을 함께 보냅니다.`
        : `${names}은(는) 입력창의 내용을 함께 보냅니다 (지금은 비어 있습니다).`,
    );
  }
  if (model.actions.length > 0) lines.push('행동을 길게 누르면 설명이 나옵니다.');
  return lines;
}

/** The Mac's own tooltip for an action, one line at a time. */
function actionLines(action: StyleAction): string[] {
  const lines: string[] = [];
  if (action.help) lines.push(action.help);
  if (action.scope) lines.push(`범위: ${action.scope}`);
  if (action.flags.includes('userInvoked')) lines.push('사람만 부를 수 있는 행동');
  if (action.flags.includes('readOnly')) lines.push('읽기 전용');
  if (action.takesText) lines.push('입력창의 내용을 함께 보냅니다.');
  if (action.requiresText) lines.push('입력창이 비어 있으면 누를 수 없습니다.');
  return lines;
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    panel: {
      backgroundColor: palette.surface,
      borderRadius: radius.md,
      gap: spacing.xs,
      marginHorizontal: spacing.lg,
      marginBottom: spacing.xs,
      padding: spacing.sm,
    },
    headRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    body: { gap: spacing.xs },
    busyRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
    headTitle: { flex: 1, fontSize: 13, fontWeight: '700' },
    sourceBadge: {
      borderColor: palette.warning,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.warning,
      fontSize: 10,
      paddingHorizontal: spacing.xs,
      paddingVertical: 1,
    },
    busyText: { flexShrink: 1 },
    stepper: { alignItems: 'center', flexDirection: 'row', gap: 4, paddingVertical: 2 },
    step: { backgroundColor: palette.border, borderRadius: 2, flex: 1, height: 4 },
    stepDone: { opacity: 0.45 },
    stepCurrent: { borderRadius: 4, flex: 1.6, height: 8 },
    stepCount: {
      borderRadius: radius.sm,
      borderWidth: 1,
      flexShrink: 0,
      fontSize: 12,
      fontVariant: ['tabular-nums'],
      fontWeight: '700',
      marginLeft: spacing.xs,
      paddingHorizontal: spacing.xs,
      paddingVertical: 1,
    },
    setup: { gap: 2 },
    command: {
      ...monoText,
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      color: palette.text,
      padding: spacing.xs,
    },
    map: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    cell: {
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      flexGrow: 1,
      flexBasis: '46%',
      gap: 1,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    cellTitle: { color: palette.text, fontSize: 12, fontWeight: '700' },
    cellAxis: { color: palette.textFaint, fontSize: 10 },
    question: { color: palette.textMuted, fontSize: 12 },
    attachments: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      padding: spacing.xs,
    },
    attachmentRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    attachmentItem: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
    attachmentDetail: { color: palette.textFaint, fontSize: 10 },
    attachmentTitle: { color: palette.textMuted, fontSize: 11 },
    attachmentMark: { color: palette.textFaint, fontSize: 10 },
    actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    hint: { color: palette.textFaint, fontSize: 11 },
    warning: { color: palette.warning, fontSize: 12 },
    pressed: { opacity: 0.7 },
  });
