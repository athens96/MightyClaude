import { useEffect, useRef, useState, type ReactElement } from 'react';
import {
  Animated,
  Easing,
  FlatList,
  Pressable,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import type { MobileBlock, MobileMightyRun } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { NoticeReply, RunHead } from '@/components/background-run';
import { StatusChip } from '@/components/status-chip';
import { EmptyState } from '@/components/ui';
import { formatDuration } from '@/components/log-entry-view';
import type { EndScrollable, FollowBottomProps } from '@/hooks/use-follow-bottom';
import { FOLDED_REPLY_LINES, backgroundLabel, shownTail } from '@/lib/background-output';
import { t } from '@/lib/i18n';
import { toneOf } from '@/lib/status-tone';
import { runTally, timelineNode, type TimelineNode } from '@/lib/timeline';
import {
  followRunIndex,
  requestOrdinals,
  blockActivity,
  blockGist,
  blockKindLabel,
  blockKindMark,
  blockPrompt,
  blockTitle,
  resultPreview,
  awaitedTaskWord,
  awaitingProgress,
  resultCardHeading,
  runHeading,
  runFold,
  runPreview,
  runResult,
} from '@/lib/mighty';
import {
  blockColor,
  cardShadow,
  monoText,
  radius,
  spacing,
  toneColors,
  typeScale,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/**
 * The Mac's Mighty graph as a vertical timeline: one collapsible group per request, its
 * blocks as round nodes on a 3 px rail — each node in its block's status colour, the
 * running one spreading a ring — with the block itself as a white card beside it.
 * Deliberately not a graph — no layout, no resizing, no reference bubbles — and every
 * kind and status the contract does not list is drawn in the neutral colour with the
 * word the host sent, so a newer Mac can never break this screen.
 */

const NODE = 25;
/**
 * Where the node sits from the top of its row, level with the card's title: the head's
 * top padding (`spacing.md`), less the 3pt the node has always sat above it.
 */
const NODE_TOP = spacing.md - 3;
const NODE_CENTRE = NODE_TOP + NODE / 2;

function railColor(palette: Palette, rail: TimelineNode['rail']): string {
  return rail === 'track' ? palette.track : toneColors(palette, rail).fill;
}

/** The node's halo: a disc in the node's colour that grows and fades, over and over. */
function Ring({ color }: { color: string }) {
  const spread = useRef(new Animated.Value(0)).current;
  useEffect(() => {
    const animation = Animated.loop(
      Animated.timing(spread, {
        toValue: 1,
        duration: 1600,
        easing: Easing.out(Easing.quad),
        useNativeDriver: true,
      }),
    );
    animation.start();
    return () => animation.stop();
  }, [spread]);
  return (
    <Animated.View
      pointerEvents="none"
      style={[
        timelineStyles.ring,
        {
          backgroundColor: color,
          opacity: spread.interpolate({ inputRange: [0, 1], outputRange: [0.55, 0] }),
          transform: [{ scale: spread.interpolate({ inputRange: [0, 1], outputRange: [1, 1.95] }) }],
        },
      ]}
    />
  );
}

/**
 * The rail and node column beside one card. The rail above the node takes the colour the
 * block before it left; the rail below takes this block's (`timelineNode`).
 */
function TimelineMarker({
  block,
  above,
  first,
  last,
}: {
  block: MobileBlock;
  above: TimelineNode['rail'] | undefined;
  first: boolean;
  last: boolean;
}) {
  const palette = usePalette();
  const node = timelineNode(block);
  const colors = toneColors(palette, node.tone);
  return (
    <View style={timelineStyles.column}>
      {!first && above !== undefined ? (
        <View style={[timelineStyles.railAbove, { backgroundColor: railColor(palette, above) }]} />
      ) : null}
      {!last ? (
        <View style={[timelineStyles.railBelow, { backgroundColor: railColor(palette, node.rail) }]} />
      ) : null}
      <View style={timelineStyles.nodeBox}>
        {node.ring ? <Ring color={colors.fill} /> : null}
        <View
          style={[
            timelineStyles.node,
            { backgroundColor: colors.fill, borderColor: palette.surfaceRaised },
          ]}
        >
          <Text style={[timelineStyles.nodeMark, { color: colors.onFill }]}>{blockKindMark(block.kind)}</Text>
        </View>
      </View>
    </View>
  );
}

const timelineStyles = StyleSheet.create({
  column: { alignItems: 'center', width: NODE + 9 },
  railAbove: { height: NODE_CENTRE, left: (NODE + 9) / 2 - 1.5, position: 'absolute', top: 0, width: 3 },
  // Runs on through the row's bottom padding to meet the next row's rail.
  railBelow: {
    bottom: -spacing.sm,
    left: (NODE + 9) / 2 - 1.5,
    position: 'absolute',
    top: NODE_CENTRE,
    width: 3,
  },
  nodeBox: { alignItems: 'center', height: NODE, justifyContent: 'center', marginTop: NODE_TOP, width: NODE },
  ring: { borderRadius: NODE / 2, height: NODE, position: 'absolute', width: NODE },
  node: {
    alignItems: 'center',
    borderRadius: NODE / 2,
    borderWidth: 3,
    height: NODE,
    justifyContent: 'center',
    width: NODE,
  },
  nodeMark: { fontSize: 10, fontWeight: '800', lineHeight: 12 },
});

/**
 * One block: folded, a gist of a line or two; tapped, what it was asked, what it has
 * been doing while it runs, and what it produced. Every block opens — a block that has
 * nothing yet says so rather than refusing the tap.
 */
function BlockRow({
  block,
  runInput,
  above,
  first,
  last,
}: {
  block: MobileBlock;
  runInput: string;
  above: TimelineNode['rail'] | undefined;
  first: boolean;
  last: boolean;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const tint = blockColor(palette, block.kind);
  const duration = block.durationMs !== undefined ? formatDuration(block.durationMs) : '';
  const gist = blockGist(block, runInput);
  const tone = toneOf(block.status);
  const ringed = tone === 'run' || tone === 'wait';
  // A background run (a task block) shows its command and live lines in place of the gist.
  const run = block.kind === 'task';
  const label = backgroundLabel(block);
  const tail = run ? shownTail(block.outputTail, block.status, open) : [];
  const command = run ? (block.summary ?? '') : '';
  const runHead = command.trim().length > 0 || tail.length > 0;

  return (
    <View style={styles.timelineRow}>
      <TimelineMarker block={block} above={above} first={first} last={last} />
      <View
        style={[
          styles.block,
          ringed && { borderColor: toneColors(palette, tone).fill },
        ]}
      >
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded: open }}
          onPress={() => setOpen((value) => !value)}
          style={({ pressed }) => [styles.blockHead, pressed && styles.pressed]}
        >
          <View style={styles.blockBody}>
            <Text numberOfLines={2} style={styles.blockTitle}>
              {blockTitle(block)}
            </Text>
            <Text style={styles.blockMeta}>
              <Text style={{ color: tint }}>{blockKindMark(block.kind)} </Text>
              {blockKindLabel(block.kind, block.summary)}
              {duration ? ` · ${duration}` : ''}
            </Text>
            {label ? <Text style={styles.blockMeta}>{label}</Text> : null}
            {run && runHead ? <RunHead command={command} tail={tail} open={open} /> : null}
            {gist && !(run && runHead) ? (
              <Text numberOfLines={2} style={styles.blockSummary}>
                {gist}
              </Text>
            ) : null}
            {run && block.noticeReply ? (
              <NoticeReply text={block.noticeReply} lines={open ? undefined : FOLDED_REPLY_LINES} />
            ) : null}
            <Text style={styles.toggle}>
              {open ? t('phone.blocks.collapse') : t('phone.blocks.expand')}
            </Text>
          </View>
          <StatusChip status={block.status} />
        </Pressable>
        {open ? <BlockDetails block={block} runInput={runInput} runHead={run && (runHead || !!block.noticeReply)} /> : null}
      </View>
    </View>
  );
}

function BlockDetails({ block, runInput, runHead }: { block: MobileBlock; runInput: string; runHead: boolean }) {
  const styles = useStyles(makeStyles);
  // A background run's command is already shown above, in its head.
  const prompt = block.kind === 'task' ? '' : blockPrompt(block, runInput);
  const activity = blockActivity(block);

  return (
    <View style={styles.details}>
      {prompt ? (
        <View style={styles.section}>
          <Text style={styles.sectionLabel}>{t('phone.blocks.prompt')}</Text>
          <AssistantMarkdown compact text={prompt} />
        </View>
      ) : null}
      {activity.length > 0 ? (
        <View style={styles.section}>
          <Text style={styles.sectionLabel}>{t('phone.blocks.activity')}</Text>
          {activity.map((line, index) => (
            <Text key={index} selectable style={styles.activityLine}>
              {`· ${line}`}
            </Text>
          ))}
        </View>
      ) : null}
      {block.output ? (
        <View style={styles.section}>
          <Text style={styles.sectionLabel}>{t('phone.blocks.result')}</Text>
          <AssistantMarkdown compact text={block.output} />
        </View>
      ) : null}
      {!prompt && !runHead && activity.length === 0 && !block.output ? (
        <Text style={styles.sectionLabel}>{t('phone.blocks.nothing')}</Text>
      ) : null}
    </View>
  );
}

/**
 * The run's final answer, set apart from the blocks the way the Mac's result block is: a
 * white card under a header strip in the run's colour — green once it finished, the
 * status colour when it ended in an error or was stopped. A long answer shows its head
 * until asked for the rest.
 */
function ResultCard({ run, text }: { run: MobileMightyRun; text: string }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [full, setFull] = useState(false);
  const { preview, folded } = resultPreview(text);
  // Awaiting its background work's notices, the card is the 알림 대기 block: the wait strip, and the work's rows under the answer so far.
  const awaiting = run.status === 'completed' ? run.awaitingNotice : undefined;
  const colors = toneColors(palette, awaiting ? 'wait' : toneOf(run.status));

  return (
    <View style={styles.resultCard}>
      <View style={[styles.resultHead, { backgroundColor: colors.fill }]}>
        <Text style={[styles.resultHeading, { color: colors.onFill }]}>
          {resultCardHeading(run)}
        </Text>
      </View>
      <View style={styles.resultBody}>
        <AssistantMarkdown selectable text={full ? text : preview} />
        {folded ? (
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ expanded: full }}
            hitSlop={8}
            onPress={() => setFull((value) => !value)}
            style={({ pressed }) => pressed && styles.pressed}
          >
            <Text style={styles.toggle}>
              {full ? t('phone.blocks.resultLess') : t('phone.blocks.resultMore')}
            </Text>
          </Pressable>
        ) : null}
        {awaiting ? (
          <View style={styles.awaiting} testID="mighty-awaiting">
            {awaiting.tasks.map((task, index) => (
              <View key={index} style={styles.awaitingRow}>
                <Text style={styles.awaitingTitle} numberOfLines={1}>{task.title}</Text>
                <Text style={styles.awaitingWord}>{awaitedTaskWord(task.status)}</Text>
              </View>
            ))}
            <Text style={[styles.awaitingProgress, { color: toneColors(palette, 'wait').ink }]}>{awaitingProgress(awaiting)}</Text>
          </View>
        ) : null}
      </View>
    </View>
  );
}

function RunGroup({ run, ordinal, newest }: {
  run: MobileMightyRun;
  /** The request's number; a notice run (Claude's reply to finished background work) has none to show. */
  ordinal: number;
  /** The newest request starts open while it runs; see `runFold`. */
  newest: boolean;
}) {
  const styles = useStyles(makeStyles);
  // The fold follows the run (a request that just finished folds its steps away)
  // until the user opens or folds it by hand.
  const fold = runFold(run, newest);
  const [openChoice, setOpen] = useState<boolean>();
  const [stepsChoice, setStepsOpen] = useState<boolean>();
  const open = openChoice ?? fold.open;
  const stepsOpen = stepsChoice ?? fold.stepsOpen;
  const preview = runPreview(run.input);
  const result = runResult(run);
  const tally = runTally(run);
  const nodes = run.blocks.map(timelineNode);

  return (
    <View style={styles.run}>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen(!open)}
        style={({ pressed }) => [styles.runHead, pressed && styles.pressed]}
      >
        <View style={styles.runTitleRow}>
          <Text style={styles.runChevron}>{open ? '▾' : '▸'}</Text>
          <Text numberOfLines={2} style={styles.runTitle}>
            {runHeading(run, ordinal)}
          </Text>
          <StatusChip status={run.status} />
        </View>
        {preview ? (
          <Text numberOfLines={2} style={styles.runPreview}>
            {preview}
          </Text>
        ) : null}
        <Text style={styles.runCount}>
          {t('phone.blocks.tally', { total: tally.total, settled: tally.settled })}
        </Text>
      </Pressable>
      {open ? (
        <View style={styles.blocks}>
          {result ? (
            <Pressable
              accessibilityRole="button"
              accessibilityState={{ expanded: stepsOpen }}
              onPress={() => setStepsOpen(!stepsOpen)}
              style={({ pressed }) => [styles.stepsRow, pressed && styles.pressed]}
            >
              <Text style={styles.runChevron}>{stepsOpen ? '▾' : '▸'}</Text>
              <Text style={styles.stepsLabel}>{t('phone.blocks.steps', { count: tally.total })}</Text>
              <Text style={styles.stepsToggle}>
                {stepsOpen ? t('phone.blocks.collapse') : t('phone.blocks.expand')}
              </Text>
            </Pressable>
          ) : null}
          {!stepsOpen ? null : run.blocks.length === 0 ? (
            <Text style={styles.runCount}>{t('phone.blocks.none')}</Text>
          ) : (
            <>
              {run.omittedBlocks ? (
                <Text style={styles.runCount}>
                  {t('phone.blocks.omitted', { count: run.omittedBlocks })}
                </Text>
              ) : null}
              {run.blocks.map((block, position) => (
                <BlockRow
                  key={block.id}
                  block={block}
                  runInput={run.input}
                  above={position > 0 ? nodes[position - 1]?.rail : undefined}
                  first={position === 0}
                  last={position === run.blocks.length - 1}
                />
              ))}
            </>
          )}
          {result ? <ResultCard run={run} text={result} /> : null}
        </View>
      ) : null}
    </View>
  );
}

/**
 * The runs as a virtualised list: a long Mighty pane can carry twenty groups of two
 * hundred rows, and mounting all of them at once is what a `ScrollView` would do. The
 * screen's header and footer ride along so the whole body stays one scroller, and the
 * screen's follow handlers keep it on the newest run while that run grows.
 */
export function MightyRunList({
  runs,
  header,
  footer,
  contentContainerStyle,
  headerStyle,
  listRef,
  follow,
}: {
  runs: MobileMightyRun[];
  header?: ReactElement | null;
  footer?: ReactElement | null;
  contentContainerStyle?: StyleProp<ViewStyle>;
  /** With a growing header in a bottom-anchored container, the runs sit above the composer. */
  headerStyle?: StyleProp<ViewStyle>;
  listRef?: (list: EndScrollable | null) => void;
  follow?: FollowBottomProps;
}) {
  const styles = useStyles(makeStyles);
  // The run followed as newest: the newest request, or a notice run after it that is a turn of its own.
  const followed = followRunIndex(runs);
  const ordinals = requestOrdinals(runs);

  return (
    <FlatList
      {...follow}
      ref={listRef}
      data={runs}
      keyExtractor={(run) => run.id}
      renderItem={({ item, index }) => (
        <View style={styles.listItem}>
          <RunGroup run={item} ordinal={ordinals[index] ?? index + 1} newest={index === followed} />
        </View>
      )}
      contentContainerStyle={contentContainerStyle}
      keyboardShouldPersistTaps="handled"
      ListHeaderComponent={header}
      ListHeaderComponentStyle={headerStyle}
      ListEmptyComponent={
        <EmptyState
          title={t('phone.blocks.empty.title')}
          description={t('phone.blocks.empty.description')}
        />
      }
      ListFooterComponent={footer}
    />
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    listItem: { paddingBottom: spacing.sm },
    // A request is a card of its own: its heading, then its timeline.
    run: {
      ...cardShadow,
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.card,
      paddingHorizontal: spacing.md,
    },
    runHead: { gap: spacing.xs, paddingHorizontal: spacing.xs, paddingVertical: spacing.md },
    runTitleRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    runChevron: { color: palette.textFaint, fontSize: 12 },
    runTitle: { ...typeScale.heading, color: palette.text, flex: 1, fontSize: 17, lineHeight: 22 },
    runPreview: { color: palette.textMuted, fontSize: 13, lineHeight: 19 },
    runCount: { color: palette.textFaint, fontSize: 11.5, fontWeight: '600' },
    blocks: { paddingBottom: spacing.md },
    // A finished request's steps, folded into one row above its result.
    stepsRow: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.sm,
      minHeight: 44,
      paddingHorizontal: spacing.xs,
    },
    stepsLabel: { color: palette.textMuted, flex: 1, fontSize: 13, fontWeight: '600' },
    stepsToggle: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    timelineRow: { flexDirection: 'row', gap: spacing.md, paddingBottom: spacing.sm },
    block: {
      backgroundColor: palette.surface,
      borderColor: 'transparent',
      borderRadius: radius.lg,
      borderWidth: 2,
      flex: 1,
      overflow: 'hidden',
    },
    blockHead: {
      alignItems: 'flex-start',
      flexDirection: 'row',
      gap: spacing.sm,
      minHeight: 44,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.md,
    },
    blockBody: { flex: 1, gap: spacing.xs },
    blockTitle: { color: palette.text, fontSize: 14, fontWeight: '700', lineHeight: 19 },
    blockMeta: { color: palette.textMuted, fontSize: 11.5 },
    blockSummary: { color: palette.textMuted, fontSize: 12, lineHeight: 17, marginTop: spacing.xs },
    toggle: { color: palette.accent, fontSize: 12, fontWeight: '700', marginTop: spacing.xs },
    details: {
      gap: spacing.sm,
      paddingBottom: spacing.md,
      paddingHorizontal: spacing.md,
      paddingTop: spacing.xs,
    },
    section: { gap: spacing.xs },
    sectionLabel: { color: palette.textFaint, fontSize: 11, fontWeight: '700' },
    activityLine: { ...monoText, color: palette.textMuted },
    resultCard: {
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      marginTop: spacing.xs,
      overflow: 'hidden',
    },
    resultHead: { paddingHorizontal: spacing.lg, paddingVertical: spacing.sm },
    resultHeading: { fontSize: 12.5, fontWeight: '800' },
    resultBody: { gap: spacing.xs, paddingHorizontal: spacing.lg, paddingVertical: spacing.md },
    awaiting: { gap: spacing.xs, paddingTop: spacing.sm, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: palette.border },
    awaitingRow: { flexDirection: 'row', alignItems: 'baseline', gap: spacing.sm },
    awaitingTitle: { flex: 1, color: palette.text, fontSize: 12.5 },
    awaitingWord: { color: palette.textMuted, fontSize: 11 },
    awaitingProgress: { fontSize: 11, fontWeight: '700' },
    pressed: { opacity: 0.6 },
  });
