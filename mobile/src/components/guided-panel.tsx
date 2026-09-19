import { useEffect, useMemo, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import type { StyleAction, StylePanel } from '@/api/types';
import { GuidedActionChip } from '@/components/guided-action-chip';
import { ActionListSheet, InfoSheet } from '@/components/sheets';
import { Button } from '@/components/ui';
import { styleViewModel } from '@/lib/styles';
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
  onRun,
}: {
  panel: StylePanel;
  /** Whether the composer has something the text-taking actions would carry. */
  hasText: boolean;
  /** The action currently in flight, if any. */
  busyActionId?: string;
  disabled: boolean;
  onRun: (actionId: string) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [chosenGroup, setChosenGroup] = useState<string | undefined>(undefined);
  const [moreOpen, setMoreOpen] = useState(false);
  const [detail, setDetail] = useState<StyleAction | undefined>(undefined);

  // A pane that switches style keeps this component mounted, so the group the user was
  // looking at has to go with the style it belonged to.
  const styleId = panel.style.id;
  useEffect(() => {
    setChosenGroup(undefined);
    setMoreOpen(false);
    setDetail(undefined);
  }, [styleId]);

  const model = useMemo(() => styleViewModel(panel, chosenGroup), [chosenGroup, panel]);
  const tint = tintColor(palette, model.tint);
  // Nothing can run until the Mac reports the style ready, so the chips say so by being
  // unpressable rather than by the host answering 409 a moment later.
  const locked = disabled || model.setup !== undefined;
  const bottom = bottomLine(model.guidance, model.takesText, hasText);

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

      {model.phase && model.phase.count > 1 ? (
        <View style={styles.stepper}>
          {Array.from({ length: model.phase.count }, (_, index) => (
            <View
              key={index}
              style={[
                styles.step,
                { backgroundColor: index <= (model.phase?.index ?? 0) ? tint : palette.border },
              ]}
            />
          ))}
          <Text style={styles.stepCount}>
            {model.phase.index + 1}/{model.phase.count}
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
                onPress={() => setChosenGroup(group.id)}
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
              </View>
            ))}
          </View>
        </ScrollView>
      ) : null}

      {model.actions.length === 0 ? (
        <Text style={styles.hint}>지금은 고를 행동이 없습니다. 아래에 적어 그대로 보내세요.</Text>
      ) : (
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
      )}

      {bottom ? <Text style={styles.hint}>{bottom}</Text> : null}

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
 * The panel's last line: the manifest's own guidance when it wrote one, and otherwise
 * which of the actions on screen would carry what is in the composer. Neither: no line.
 */
function bottomLine(
  guidance: string | undefined,
  takesText: readonly StyleAction[],
  hasText: boolean,
): string | undefined {
  if (guidance) return guidance;
  if (takesText.length === 0) return undefined;
  const names = takesText.map((action) => action.title).join(', ');
  return hasText
    ? `${names}은(는) 입력창의 내용을 함께 보냅니다.`
    : `${names}은(는) 입력창의 내용을 함께 보냅니다 (지금은 비어 있습니다).`;
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
    stepper: { alignItems: 'center', flexDirection: 'row', gap: 3 },
    step: { borderRadius: 2, flex: 1, height: 3 },
    stepCount: { color: palette.textFaint, fontSize: 10, marginLeft: spacing.xs },
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
    actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    hint: { color: palette.textFaint, fontSize: 11 },
    warning: { color: palette.warning, fontSize: 12 },
    pressed: { opacity: 0.7 },
  });
