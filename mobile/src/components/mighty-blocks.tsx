import { useState, type ReactElement } from 'react';
import {
  FlatList,
  Pressable,
  StyleSheet,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import type { MobileBlock, MobileMightyRun } from '@/api/types';
import { StatusChip } from '@/components/status-chip';
import { EmptyState } from '@/components/ui';
import { formatDuration } from '@/components/log-entry-view';
import { blockKindLabel, blockKindMark, blockTitle, runHeading, runPreview } from '@/lib/mighty';
import {
  blockColor,
  monoText,
  radius,
  spacing,
  statusColor,
  statusLabel,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/**
 * The Mac's Mighty graph as a list: one collapsible group per request, its blocks as
 * rows. Deliberately not a graph — no layout, no resizing, no reference bubbles — and
 * every kind and status the contract does not list is drawn in the neutral colour with
 * the word the host sent, so a newer Mac can never break this screen.
 */
function BlockRow({ block }: { block: MobileBlock }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const tint = blockColor(palette, block.kind);
  const duration = block.durationMs !== undefined ? formatDuration(block.durationMs) : '';
  const expandable = Boolean(block.output);

  return (
    <View style={[styles.block, { borderLeftColor: tint }]}>
      <Pressable
        accessibilityRole={expandable ? 'button' : undefined}
        accessibilityState={expandable ? { expanded: open } : undefined}
        disabled={!expandable}
        onPress={() => setOpen((value) => !value)}
        style={({ pressed }) => [styles.blockHead, pressed && expandable && styles.pressed]}
      >
        <Text style={[styles.mark, { color: tint }]}>{blockKindMark(block.kind)}</Text>
        <View style={styles.blockBody}>
          <Text numberOfLines={2} style={styles.blockTitle}>
            {blockTitle(block)}
          </Text>
          <Text style={styles.blockMeta}>
            {blockKindLabel(block.kind)}
            {duration ? ` · ${duration}` : ''}
          </Text>
          {block.summary ? (
            <Text numberOfLines={3} style={styles.blockSummary}>
              {block.summary}
            </Text>
          ) : null}
          {expandable ? (
            <Text style={styles.toggle}>{open ? '내용 접기' : '내용 보기'}</Text>
          ) : null}
        </View>
        <Text style={[styles.blockStatus, { color: statusColor(palette, block.status) }]}>
          {statusLabel(block.status)}
        </Text>
      </Pressable>
      {open && block.output ? (
        <Text selectable style={styles.output}>
          {block.output}
        </Text>
      ) : null}
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
        <Text style={styles.runCount}>블록 {run.blocks.length}개</Text>
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
              {run.blocks.map((block) => (
                <BlockRow key={block.id} block={block} />
              ))}
            </>
          )}
        </View>
      ) : null}
    </View>
  );
}

/**
 * The runs as a virtualised list: a long Mighty pane can carry twenty groups of two
 * hundred rows, and mounting all of them at once is what a `ScrollView` would do. The
 * screen's header and footer ride along so the whole body stays one scroller.
 */
export function MightyRunList({
  runs,
  header,
  footer,
  contentContainerStyle,
}: {
  runs: MobileMightyRun[];
  header?: ReactElement | null;
  footer?: ReactElement | null;
  contentContainerStyle?: StyleProp<ViewStyle>;
}) {
  const styles = useStyles(makeStyles);
  const last = runs.length - 1;

  return (
    <FlatList
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
    run: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      overflow: 'hidden',
    },
    runHead: { gap: spacing.xs, padding: spacing.md },
    runTitleRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    runChevron: { color: palette.textFaint, fontSize: 12 },
    runTitle: { color: palette.text, flex: 1, fontSize: 14, fontWeight: '700' },
    runPreview: { color: palette.textMuted, fontSize: 13 },
    runCount: { color: palette.textFaint, fontSize: 11 },
    blocks: {
      borderTopColor: palette.border,
      borderTopWidth: StyleSheet.hairlineWidth,
      gap: spacing.xs,
      padding: spacing.sm,
    },
    block: {
      backgroundColor: palette.surfaceRaised,
      borderLeftWidth: 3,
      borderRadius: radius.sm,
      overflow: 'hidden',
    },
    blockHead: {
      alignItems: 'flex-start',
      flexDirection: 'row',
      gap: spacing.sm,
      padding: spacing.sm,
    },
    mark: { fontSize: 13, marginTop: 1, width: 14 },
    blockBody: { flex: 1, gap: 2 },
    blockTitle: { color: palette.text, fontSize: 13, fontWeight: '600' },
    blockMeta: { color: palette.textFaint, fontSize: 11 },
    blockSummary: { color: palette.textMuted, fontSize: 12 },
    blockStatus: { fontSize: 11, fontWeight: '700' },
    toggle: { color: palette.accent, fontSize: 11, marginTop: 2 },
    output: {
      ...monoText,
      backgroundColor: palette.surface,
      borderTopColor: palette.border,
      borderTopWidth: StyleSheet.hairlineWidth,
      color: palette.textMuted,
      padding: spacing.sm,
    },
    pressed: { opacity: 0.7 },
  });
