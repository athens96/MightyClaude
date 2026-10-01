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
import { StatusChip } from '@/components/status-chip';
import { EmptyState } from '@/components/ui';
import { formatDuration } from '@/components/log-entry-view';
import type { EndScrollable, FollowBottomProps } from '@/hooks/use-follow-bottom';
import { t } from '@/lib/i18n';
import { toneOf } from '@/lib/status-tone';
import { runTally, timelineNode, type TimelineNode } from '@/lib/timeline';
import {
  blockActivity,
  blockGist,
  blockKindLabel,
  blockKindMark,
  blockPrompt,
  blockTitle,
  resultPreview,
  runHeading,
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
/** Where the node sits from the top of its row, level with the card's title. */
const NODE_TOP = 9;
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
              {blockKindLabel(block.kind)}
              {duration ? ` · ${duration}` : ''}
            </Text>
            {gist ? (
              <Text numberOfLines={2} style={styles.blockSummary}>
                {gist}
              </Text>
            ) : null}
            <Text style={styles.toggle}>
              {open ? t('phone.blocks.collapse') : t('phone.blocks.expand')}
            </Text>
          </View>
          <StatusChip status={block.status} />
        </Pressable>
        {open ? <BlockDetails block={block} runInput={runInput} /> : null}
      </View>
    </View>
  );
}

function BlockDetails({ block, runInput }: { block: MobileBlock; runInput: string }) {
  const styles = useStyles(makeStyles);
  const prompt = blockPrompt(block, runInput);
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
      {!prompt && activity.length === 0 && !block.output ? (
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
  const colors = toneColors(palette, toneOf(run.status));

  return (
    <View style={styles.resultCard}>
      <View style={[styles.resultHead, { backgroundColor: colors.fill }]}>
        <Text style={[styles.resultHeading, { color: colors.onFill }]}>
          {run.status === 'completed' ? `✓ ${t('phone.blocks.finalResult')}` : t('phone.blocks.finalResult')}
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
      </View>
    </View>
  );
}

function RunGroup({ run, index, initiallyOpen }: {
  run: MobileMightyRun;
  index: number;
  /** The newest request starts open; the rest are folded away. */
  initiallyOpen: boolean;
}) {
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(initiallyOpen);
  const preview = runPreview(run.input);
  const result = runResult(run);
  const tally = runTally(run);
  const nodes = run.blocks.map(timelineNode);

  return (
    <View style={styles.run}>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded: open }}
        onPress={() => setOpen((value) => !value)}
        style={({ pressed }) => [styles.runHead, pressed && styles.pressed]}
      >
        <View style={styles.runTitleRow}>
          <Text style={styles.runChevron}>{open ? '▾' : '▸'}</Text>
          <Text numberOfLines={2} style={styles.runTitle}>
            {runHeading(run, index)}
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
          {run.blocks.length === 0 ? (
            <Text style={styles.runCount}>아직 블록이 없습니다.</Text>
          ) : (
            <>
              {run.omittedBlocks ? (
                <Text style={styles.runCount}>이전 블록 {run.omittedBlocks}개 생략</Text>
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
  const last = runs.length - 1;

  return (
    <FlatList
      {...follow}
      ref={listRef}
      data={runs}
      keyExtractor={(run) => run.id}
      renderItem={({ item, index }) => (
        <View style={styles.listItem}>
          <RunGroup run={item} index={index} initiallyOpen={index === last} />
        </View>
      )}
      contentContainerStyle={contentContainerStyle}
      keyboardShouldPersistTaps="handled"
      ListHeaderComponent={header}
      ListHeaderComponentStyle={headerStyle}
      ListEmptyComponent={
        <EmptyState title="요청이 없습니다" description="메시지를 보내면 블록이 쌓입니다." />
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
      paddingHorizontal: spacing.sm + 2,
    },
    runHead: { gap: 3, paddingHorizontal: spacing.xs, paddingVertical: spacing.md },
    runTitleRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    runChevron: { color: palette.textFaint, fontSize: 12 },
    runTitle: { ...typeScale.heading, color: palette.text, flex: 1, fontSize: 17, lineHeight: 22 },
    runPreview: { color: palette.textMuted, fontSize: 13, lineHeight: 19 },
    runCount: { color: palette.textFaint, fontSize: 11.5, fontWeight: '600' },
    blocks: { paddingBottom: spacing.md },
    timelineRow: { flexDirection: 'row', gap: 6, paddingBottom: spacing.sm },
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
      paddingVertical: spacing.sm + 1,
    },
    blockBody: { flex: 1, gap: 2 },
    blockTitle: { color: palette.text, fontSize: 14, fontWeight: '700', lineHeight: 19 },
    blockMeta: { color: palette.textMuted, fontSize: 11.5 },
    blockSummary: { color: palette.textMuted, fontSize: 12, lineHeight: 17, marginTop: 2 },
    toggle: { color: palette.accent, fontSize: 12, fontWeight: '700', marginTop: 3 },
    details: {
      gap: spacing.sm,
      paddingBottom: spacing.sm + 2,
      paddingHorizontal: spacing.md,
      paddingTop: spacing.xs,
    },
    section: { gap: 2 },
    sectionLabel: { color: palette.textFaint, fontSize: 11, fontWeight: '700' },
    activityLine: { ...monoText, color: palette.textMuted },
    resultCard: {
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      marginTop: spacing.xs,
      overflow: 'hidden',
    },
    resultHead: { paddingHorizontal: spacing.md + 2, paddingVertical: spacing.sm },
    resultHeading: { fontSize: 12.5, fontWeight: '800' },
    resultBody: { gap: spacing.xs, paddingHorizontal: spacing.md + 2, paddingVertical: spacing.sm + 2 },
    pressed: { opacity: 0.6 },
  });
