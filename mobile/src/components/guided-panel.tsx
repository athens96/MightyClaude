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
import type { StyleAction, StylePanel, StyleTaskItem, StyleWidget } from '@/api/types';
import { GuidedActionChip } from '@/components/guided-action-chip';
import { ActionListSheet, InfoSheet } from '@/components/sheets';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { progressBarDisplay, styleViewModel, taskRowDisplay, type StyleViewModel } from '@/lib/styles';
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
            <Text style={[styles.stepCount, { color: tint }]}>
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
                <Text style={styles.hint}>{t('phone.guided.runOnMac')}</Text>
              </>
            ) : null}
          </View>
        ) : null}

        {/* §1.16: the Mac read the style's declared state sources and computed these;
            the phone draws the same three kinds, in the same order, and nothing else. */}
        {model.widgets.length > 0 ? (
          <View style={styles.widgets}>
            {model.widgets.map((widget, index) => (
              <StateWidget key={index} widget={widget} tint={tint} />
            ))}
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
                    <Text accessibilityLabel={t('styles.action.readOnly')} style={styles.attachmentMark}>
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
                {t('phone.guided.running')}
              </Text>
            </View>
          ) : (
            <Text style={styles.hint}>{t('phone.guided.noActions')}</Text>
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
                label={t('composer.more')}
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
        title={t('phone.guided.actionsTitle', { name: panel.style.name })}
        note={t('phone.guided.actionsNote')}
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
 * One computed state widget. The kinds are closed — progress bar, list, label — so there
 * is nothing to guess here: a payload carrying anything else was already dropped by
 * `normalizeStylePanel`, and the Mac sends the values themselves, never a template.
 */
export function StateWidget({ widget, tint }: { widget: StyleWidget; tint: string }) {
  const styles = useStyles(makeStyles);
  if (widget.kind === 'progressBar') {
    // Both numbers are counts, so the bar says 3/7 rather than 43% (§1.16.4).
    const { fraction, text: count } = progressBarDisplay(widget);
    const percent = Math.round(fraction * 100);
    return (
      <View accessible accessibilityLabel={count} style={styles.widgetRow}>
        <View style={styles.widgetTrack}>
          <View style={[styles.widgetFill, { backgroundColor: tint, width: `${percent}%` }]} />
        </View>
        <Text style={[styles.widgetCount, { color: tint }]}>{count}</Text>
      </View>
    );
  }
  // An empty list, label or task list never reaches here: `styleViewModel` left it out (§1.16.4).
  if (widget.kind === 'taskList') return <TaskListWidget items={widget.items} tint={tint} />;
  if (widget.kind === 'list') {
    return (
      <View style={styles.widgetList}>
        {widget.items.map((item, index) => (
          <Text key={index} numberOfLines={1} style={styles.widgetItem}>
            {item}
          </Text>
        ))}
      </View>
    );
  }
  // One line, as on the Mac (`.lineLimit(1)`): a long label ends in an ellipsis.
  return (
    <Text numberOfLines={1} ellipsizeMode="tail" style={styles.widgetLabel}>
      {widget.text}
    </Text>
  );
}

/**
 * Background tasks (§1.17): a status dot, what each is doing, and `kind · status ·
 * elapsed`. The elapsed time ticks once a second while any task still runs.
 */
export function TaskListWidget({ items, tint }: { items: StyleTaskItem[]; tint: string }) {
  const styles = useStyles(makeStyles);
  const palette = usePalette();
  const [now, setNow] = useState(() => Date.now());
  const anyRunning = items.some((item) => item.status === 'running');
  useEffect(() => {
    if (!anyRunning) return undefined;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [anyRunning]);
  return (
    <View style={styles.widgetList}>
      {items.map((item, index) => {
        const row = taskRowDisplay(item, now);
        const dot =
          item.status === 'running'
            ? tint
            : item.status === 'completed'
              ? palette.success
              : item.status === 'failed'
                ? palette.danger
                : palette.textMuted;
        return (
          <View key={index} accessible accessibilityLabel={`${row.text} · ${row.detail}`} style={styles.taskRow}>
            <View style={[styles.taskDot, { backgroundColor: dot }]} />
            <Text numberOfLines={1} style={styles.taskText}>
              {row.text}
            </Text>
            <Text numberOfLines={1} style={styles.taskDetail}>
              {row.detail}
            </Text>
          </View>
        );
      })}
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
        ? t('phone.guided.takesText', { names })
        : t('phone.guided.takesTextEmpty', { names }),
    );
  }
  if (model.actions.length > 0) lines.push(t('phone.guided.longPressHint'));
  return lines;
}

/** The Mac's own tooltip for an action, one line at a time. */
function actionLines(action: StyleAction): string[] {
  const lines: string[] = [];
  if (action.help) lines.push(action.help);
  if (action.scope) lines.push(t('styles.action.scope', { scope: action.scope }));
  if (action.flags.includes('userInvoked')) lines.push(t('phone.guided.action.userInvoked'));
  if (action.flags.includes('readOnly')) lines.push(t('styles.action.readOnly'));
  if (action.takesText) lines.push(t('phone.guided.action.takesText'));
  if (action.requiresText) lines.push(t('phone.guided.action.requiresText'));
  return lines;
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    panel: {
      backgroundColor: palette.surface,
      borderRadius: radius.lg,
      gap: spacing.sm,
      marginHorizontal: spacing.lg,
      marginBottom: spacing.xs,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    headRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    body: { gap: spacing.xs },
    busyRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
    headTitle: { flex: 1, fontSize: 13, fontWeight: '600' },
    sourceBadge: { color: palette.warning, fontSize: 11, fontWeight: '500' },
    busyText: { flexShrink: 1 },
    stepper: { alignItems: 'center', flexDirection: 'row', gap: 4, paddingVertical: 2 },
    step: { backgroundColor: palette.border, borderRadius: radius.round, flex: 1, height: 3 },
    stepDone: { opacity: 0.45 },
    stepCurrent: { flex: 1.6, height: 6 },
    stepCount: {
      flexShrink: 0,
      fontSize: 12,
      fontVariant: ['tabular-nums'],
      fontWeight: '600',
      marginLeft: spacing.xs,
      paddingVertical: 1,
    },
    setup: { gap: 2 },
    widgets: { gap: spacing.xs },
    widgetRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
    widgetTrack: {
      backgroundColor: palette.border,
      borderRadius: radius.round,
      flex: 1,
      height: 4,
      overflow: 'hidden',
    },
    widgetFill: { borderRadius: radius.round, height: 4 },
    widgetCount: { fontSize: 11, fontVariant: ['tabular-nums'], fontWeight: '600' },
    widgetList: { gap: 1 },
    widgetItem: { color: palette.textMuted, fontSize: 11 },
    widgetLabel: { color: palette.textMuted, fontSize: 12 },
    taskRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
    taskDot: { borderRadius: radius.round, height: 6, width: 6 },
    taskText: { color: palette.text, flex: 1, fontSize: 12 },
    taskDetail: { color: palette.textMuted, fontSize: 11, fontVariant: ['tabular-nums'] },

    command: {
      ...monoText,
      backgroundColor: palette.background,
      borderRadius: radius.sm,
      color: palette.text,
      padding: spacing.sm,
    },
    map: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    cell: {
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      flexGrow: 1,
      flexBasis: '46%',
      gap: 1,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    cellTitle: { color: palette.text, fontSize: 12, fontWeight: '600' },
    cellAxis: { color: palette.textFaint, fontSize: 10 },
    question: { color: palette.textMuted, fontSize: 12 },
    attachments: {
      backgroundColor: palette.background,
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
    pressed: { opacity: 0.6 },
  });
