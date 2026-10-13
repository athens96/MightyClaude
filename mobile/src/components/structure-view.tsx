import {
  createContext,
  memo,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactElement,
} from 'react';
import {
  FlatList,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type LayoutChangeEvent,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
  type StyleProp,
  type ViewStyle,
} from 'react-native';
import type {
  StructureActivity,
  StructureCluster,
  StructureColumn,
  StructureItem,
  StructureNode,
  StructureRow,
} from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { StatusChip } from '@/components/status-chip';
import { EmptyState } from '@/components/ui';
import type { EndScrollable, FollowBottomProps } from '@/hooks/use-follow-bottom';
import { t } from '@/lib/i18n';
import { blockKindLabel } from '@/lib/mighty';
import { toneOf } from '@/lib/status-tone';
import {
  FOLD_LINES,
  chosenAfterMove,
  foldText,
  itemHeading,
  nodeFolds,
  nodeMeta,
  pageAtOffset,
  pageLabel,
  phaseRail,
  requestRail,
  resultHeading,
  structureNotice,
  visiblePage,
  currentPageIndex,
  type RailChip,
  type StepLook,
  type StructureState,
} from '@/lib/structure';
import {
  cardShadow,
  monoText,
  radius,
  spacing,
  statusLabel,
  toneColors,
  typeScale,
  usePalette,
  useStyles,
  type Palette,
} from '@/theme';

/**
 * The structure view (구조): the Mac's own structure of the pane. A styled pane is a phase
 * bar over one page per phase, swiped sideways, each page listing that phase's requests;
 * a pane without a style is one stacked row per request. Every phase and step comes from
 * the Mac (`MobileMighty.structure`), so the two screens always agree.
 */

/** The colours one step takes: done green, current the accent, pending grey. */
function stepColors(palette: Palette, look: StepLook): { fill: string; ink: string } {
  switch (look.tone) {
    case 'done':
      return { fill: toneColors(palette, 'done').fill, ink: toneColors(palette, 'done').onFill };
    case 'current':
      return { fill: palette.accent, ink: palette.onAccent };
    case 'err':
      return { fill: toneColors(palette, 'err').fill, ink: toneColors(palette, 'err').onFill };
    case 'stop':
      return { fill: toneColors(palette, 'stop').fill, ink: toneColors(palette, 'stop').onFill };
    default:
      return { fill: palette.track, ink: palette.textMuted };
  }
}

function Chip({ chip, selected, onPress }: { chip: RailChip; selected?: boolean; onPress?: () => void }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const colors = stepColors(palette, chip.look);
  const body = (
    <View style={[styles.chip, { backgroundColor: colors.fill }, selected && { borderColor: palette.text }]}>
      <Text style={[styles.chipMark, { color: colors.ink }]}>{chip.look.mark}</Text>
      <Text numberOfLines={1} style={[styles.chipTitle, { color: colors.ink }]}>
        {chip.title}
      </Text>
    </View>
  );
  if (!onPress) {
    return (
      <View accessible accessibilityLabel={chip.label}>
        {body}
      </View>
    );
  }
  return (
    <Pressable
      accessibilityRole="tab"
      accessibilityLabel={chip.label}
      accessibilityHint={t('phone.structure.chipHint')}
      accessibilityState={{ selected: selected === true }}
      hitSlop={{ bottom: 6, top: 6 }}
      onPress={onPress}
      style={({ pressed }) => pressed && styles.pressed}
    >
      {body}
    </Pressable>
  );
}

/** Chips joined by a line that turns green once the step before it is done. */
function Rail({
  chips,
  selected,
  onSelect,
  scrolls,
}: {
  chips: RailChip[];
  selected?: number;
  onSelect?: (index: number) => void;
  scrolls?: boolean;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const scroller = useRef<ScrollView>(null);
  /** Where each chip starts along the rail, so the selected one can be brought into view. */
  const starts = useRef(new Map<number, number>());
  useEffect(() => {
    if (!scrolls || selected === undefined) return;
    const x = starts.current.get(selected);
    if (x !== undefined) scroller.current?.scrollTo({ x: Math.max(0, x - spacing.xl), animated: true });
  }, [scrolls, selected]);
  const row = chips.map((chip, index) => (
    <View
      key={chip.id}
      onLayout={(event) => {
        starts.current.set(index, event.nativeEvent.layout.x);
        if (index === selected) scroller.current?.scrollTo({ x: Math.max(0, event.nativeEvent.layout.x - spacing.xl), animated: false });
      }}
      style={styles.railStep}
    >
      <Chip chip={chip} selected={selected === index} onPress={onSelect ? () => onSelect(index) : undefined} />
      {index < chips.length - 1 ? (
        <View
          style={[
            styles.railLine,
            { backgroundColor: chip.look.tone === 'done' ? toneColors(palette, 'done').fill : palette.track },
          ]}
        />
      ) : null}
    </View>
  ));
  if (!scrolls) {
    return <View style={styles.railRow}>{row}</View>;
  }
  return (
    <ScrollView
      ref={scroller}
      horizontal
      accessibilityLabel={t('guidedPanel.phasesAccessibility')}
      accessibilityRole="tablist"
      contentContainerStyle={styles.railRow}
      showsHorizontalScrollIndicator={false}
    >
      {row}
    </ScrollView>
  );
}

/** A text folded to its first lines, with "전체 보기 / 접기" when there is more. */
function FoldableText({
  text,
  lines,
  markdown,
  label,
  muted,
}: {
  text: string;
  lines: number;
  markdown?: boolean;
  label: string;
  muted?: boolean;
}) {
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const { preview, folded } = useMemo(() => foldText(text, lines, markdown), [text, lines, markdown]);
  const shown = open ? text : preview;
  return (
    <View style={styles.foldable}>
      {markdown ? (
        <AssistantMarkdown compact selectable text={shown} />
      ) : (
        <Text accessibilityLabel={`${label}: ${shown}`} selectable style={muted ? styles.mutedText : styles.bodyText}>
          {shown}
        </Text>
      )}
      {folded ? <FoldToggle open={open} onToggle={() => setOpen((value) => !value)} /> : null}
    </View>
  );
}

function FoldToggle({ open, onToggle }: { open: boolean; onToggle: () => void }) {
  const styles = useStyles(makeStyles);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ expanded: open }}
      hitSlop={8}
      onPress={onToggle}
      style={({ pressed }) => pressed && styles.pressed}
    >
      <Text style={styles.toggle}>{open ? t('phone.blocks.resultLess') : t('phone.blocks.resultMore')}</Text>
    </Pressable>
  );
}

function ActivityLine({ line }: { line: StructureActivity }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const color = line.state === 'error' ? palette.danger : line.state === 'running' ? palette.accent : palette.textMuted;
  return (
    <Text numberOfLines={1} selectable style={[styles.activityLine, { color }]}>
      {`· ${line.text}`}
    </Text>
  );
}

/** A sub-agent or task: its status as a bar, its title, kind and model, what it was asked and said. */
const NodeRow = memo(function NodeRow({ node }: { node: StructureNode }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const folds = nodeFolds(node);
  const prompt = node.prompt ? (open ? node.prompt : foldText(node.prompt, FOLD_LINES.prompt).preview) : undefined;
  const output = node.output ? (open ? node.output : foldText(node.output, FOLD_LINES.output).preview) : undefined;
  return (
    <View style={[styles.node, { marginLeft: node.depth * spacing.lg }]}>
      <View style={[styles.nodeBar, { backgroundColor: toneColors(palette, toneOf(node.status)).fill }]} />
      <View style={styles.nodeBody}>
        <Text
          accessibilityLabel={t('graph.structure.nodeAccessibility', {
            title: node.title || blockKindLabel(node.kind),
            status: statusLabel(node.status),
          })}
          numberOfLines={1}
          style={styles.nodeTitle}
        >
          {node.title || blockKindLabel(node.kind)}
        </Text>
        <Text numberOfLines={1} style={styles.nodeMeta}>
          {`${nodeMeta(node, blockKindLabel)} · ${statusLabel(node.status)}`}
        </Text>
        {prompt ? (
          <Text accessibilityLabel={`${t('graph.structure.prompt')}: ${prompt}`} selectable style={styles.mutedText}>
            {prompt}
          </Text>
        ) : null}
        {output ? (
          <Text accessibilityLabel={`${t('graph.structure.output')}: ${output}`} selectable style={styles.bodyText}>
            {output}
          </Text>
        ) : null}
        {node.activity ? <ActivityLine line={node.activity} /> : null}
        {folds ? <FoldToggle open={open} onToggle={() => setOpen((value) => !value)} /> : null}
      </View>
    </View>
  );
});

/** The sub-agent trees, each a dashed box under a caption naming how many blocks it holds. */
function Clusters({ clusters, omitted }: { clusters: StructureCluster[]; omitted?: number }) {
  const styles = useStyles(makeStyles);
  if (clusters.length === 0 && !omitted) return null;
  return (
    <View style={styles.clusters}>
      {clusters.map((cluster) => (
        <View key={cluster.nodes[0]?.id ?? ''} style={styles.cluster}>
          <Text style={styles.caption}>
            {t('graph.structure.clusterAccessibility', { count: cluster.nodes.length })}
          </Text>
          {cluster.nodes.map((node) => (
            <NodeRow key={node.id} node={node} />
          ))}
        </View>
      ))}
      {omitted ? <Text style={styles.note}>{t('phone.structure.nodesOmitted', { count: omitted })}</Text> : null}
    </View>
  );
}

/** The request part: what was asked. */
function RequestPart({ item, titled }: { item: StructureItem; titled: boolean }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.part}>
      {titled ? <Text style={styles.partLabel}>{t('graph.structure.column.request')}</Text> : null}
      {item.request ? (
        <FoldableText text={item.request} lines={FOLD_LINES.request} label={t('graph.structure.column.request')} />
      ) : null}
    </View>
  );
}

/** The 실행 part: the main agent's status, tally, latest reply and tool calls. */
function ExecutionPart({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.part}>
      <View style={styles.partHead}>
        <Text style={styles.partLabel}>{t('graph.structure.column.execution')}</Text>
        <StatusChip status={item.status} />
        <Text numberOfLines={1} style={styles.tally}>
          {t('graph.structure.tally', { agents: item.tally.agents, tools: item.tally.tools })}
        </Text>
      </View>
      {item.message ? (
        <FoldableText text={item.message} lines={FOLD_LINES.message} label={t('graph.structure.latest')} />
      ) : !item.finished && item.activity.length === 0 ? (
        <Text style={styles.mutedText}>{t('graph.block.waitingResponse')}</Text>
      ) : null}
      {item.activity.length > 0 ? (
        <View>
          <Text style={styles.caption}>{t('graph.structure.activity')}</Text>
          {item.activity.map((line, index) => (
            <ActivityLine key={`${index}:${line.text}`} line={line} />
          ))}
        </View>
      ) : null}
    </View>
  );
}

/** The result part: the answer as Markdown, else how the request ended or that it has not yet. */
function ResultPart({ item }: { item: StructureItem }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const color = item.finished ? toneColors(palette, toneOf(item.status)).ink : palette.textMuted;
  return (
    <View style={styles.part}>
      <Text style={[styles.partLabel, { color }]}>{resultHeading(item)}</Text>
      {item.answer ? (
        <FoldableText markdown text={item.answer} lines={FOLD_LINES.answer} label={resultHeading(item)} />
      ) : (
        <Text style={styles.mutedText}>
          {item.finished ? statusLabel(item.status) : t('graph.structure.resultPending')}
        </Text>
      )}
    </View>
  );
}

/**
 * One request of a phase page: request, 실행, clusters, and once finished its result.
 * Memoised: `receiveStructure` keeps an unchanged request the same object, so an update
 * redraws only the cards that moved.
 */
const PhaseCard = memo(function PhaseCard({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.card}>
      <Text accessibilityRole="header" numberOfLines={2} style={styles.heading}>
        {itemHeading(item)}
      </Text>
      <RequestPart item={item} titled />
      <ExecutionPart item={item} />
      <Clusters clusters={item.clusters} omitted={item.omittedNodes} />
      {item.finished ? <ResultPart item={item} /> : null}
    </View>
  );
});

/** One request of a free pane: its own small rail, then the sections stacked. Memoised as `PhaseCard`. */
const FreeRow = memo(function FreeRow({ row }: { row: StructureRow }) {
  const styles = useStyles(makeStyles);
  const chips = useMemo(() => requestRail(row), [row]);
  return (
    <View style={styles.card}>
      <Text accessibilityRole="header" numberOfLines={2} style={styles.heading}>
        {itemHeading(row.item)}
      </Text>
      <Rail chips={chips} />
      <RequestPart item={row.item} titled={false} />
      <ExecutionPart item={row.item} />
      <Clusters clusters={row.item.clusters} omitted={row.item.omittedNodes} />
      <ResultPart item={row.item} />
    </View>
  );
});

interface ListProps {
  header?: ReactElement | null;
  footer?: ReactElement | null;
  contentContainerStyle?: StyleProp<ViewStyle>;
  headerStyle?: StyleProp<ViewStyle>;
  /** Attaches the vertical list on screen, under its own key (`useFollowBottom().attachAs`). */
  attachAs: (key: string) => (list: EndScrollable | null) => void;
  follow?: FollowBottomProps;
}

/**
 * The screen's footer (permission cards, the queue), handed down through context so a
 * new footer element — the screen renders one on every keystroke — redraws only this
 * slot and never the pages or the rows.
 */
const FooterContext = createContext<{ footer: ReactElement | null | undefined; page: number }>({
  footer: undefined,
  page: 0,
});

function PageFooter({ index }: { index: number }) {
  const { footer, page } = useContext(FooterContext);
  return index === page ? (footer ?? null) : null;
}

const PAGE_FOOTERS = new Map<number, ReactElement>();
/** One stable footer element per page index, so the lists never see a new footer prop. */
function pageFooter(index: number): ReactElement {
  let element = PAGE_FOOTERS.get(index);
  if (!element) {
    element = <PageFooter index={index} />;
    PAGE_FOOTERS.set(index, element);
  }
  return element;
}

function OmittedNote({ count }: { count?: number }) {
  const styles = useStyles(makeStyles);
  return count ? <Text style={styles.note}>{t('phone.structure.omitted', { count })}</Text> : null;
}

function renderFreeRow({ item }: { item: StructureRow }) {
  return <FreeRowCell row={item} />;
}

function FreeRowCell({ row }: { row: StructureRow }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.listItem}>
      <FreeRow row={row} />
    </View>
  );
}

function freeRowKey(row: StructureRow): string {
  return row.item.runId;
}

function FreeList({ rows, omitted, ...list }: ListProps & { rows: StructureRow[]; omitted?: number }) {
  return (
    <FlatList
      {...list.follow}
      ref={list.attachAs('free')}
      data={rows}
      keyExtractor={freeRowKey}
      renderItem={renderFreeRow}
      contentContainerStyle={list.contentContainerStyle}
      keyboardShouldPersistTaps="handled"
      ListHeaderComponent={
        <View>
          {list.header}
          <OmittedNote count={omitted} />
        </View>
      }
      ListHeaderComponentStyle={list.headerStyle}
      ListEmptyComponent={
        <EmptyState title={t('phone.blocks.empty.title')} description={t('phone.blocks.empty.description')} />
      }
      ListFooterComponent={list.footer}
    />
  );
}

function renderPhaseCard({ item }: { item: StructureItem }) {
  return <PhaseCardCell item={item} />;
}

function PhaseCardCell({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.listItem}>
      <PhaseCard item={item} />
    </View>
  );
}

function itemKey(item: StructureItem): string {
  return item.runId;
}

const PhasePage = memo(function PhasePage({
  column,
  index,
  width,
  omitted,
  visible,
  contentContainerStyle,
  attachAs,
  follow,
}: {
  column: StructureColumn;
  index: number;
  width: number;
  omitted?: number;
  visible: boolean;
  contentContainerStyle?: StyleProp<ViewStyle>;
  attachAs: ListProps['attachAs'];
  follow?: FollowBottomProps;
}) {
  const styles = useStyles(makeStyles);
  return (
    <View style={{ width }}>
      <FlatList
        {...(visible ? follow : undefined)}
        ref={visible ? attachAs(`phase:${column.id}`) : undefined}
        data={column.items}
        keyExtractor={itemKey}
        renderItem={renderPhaseCard}
        contentContainerStyle={contentContainerStyle}
        keyboardShouldPersistTaps="handled"
        nestedScrollEnabled
        ListHeaderComponent={<OmittedNote count={omitted} />}
        ListEmptyComponent={<Text style={styles.note}>{t('phone.structure.emptyPhase')}</Text>}
        ListFooterComponent={pageFooter(index)}
      />
    </View>
  );
});

/** A phase bar over one page per phase; the view opens on the current phase. */
function PhasePager({ columns, omitted, ...list }: ListProps & { columns: StructureColumn[]; omitted?: number }) {
  const styles = useStyles(makeStyles);
  const screen = useWindowDimensions();
  const [width, setWidth] = useState(screen.width);
  /**
   * The phase the user swiped or tapped to, by id; none until then and again once they
   * come back to the current phase, so the pager follows the phase while it is unset.
   */
  const [chosen, setChosen] = useState<string | undefined>(undefined);
  const pager = useRef<FlatList<StructureColumn>>(null);
  const page = visiblePage(columns, chosen);
  const pageRef = useRef(page);
  pageRef.current = page;
  const current = currentPageIndex(columns);
  const chips = useMemo(() => phaseRail(columns), [columns]);
  // The pages redraw when the page on screen (it carries the follow handlers) or the
  // width moves; the footer reaches its page through context instead.
  const pagesState = useMemo(() => ({ page, width }), [page, width]);
  const footer = useMemo(() => ({ footer: list.footer, page }), [list.footer, page]);

  const goTo = useCallback(
    (index: number, animated: boolean) => {
      if (index < 0 || index >= columns.length) return;
      pager.current?.scrollToOffset({ offset: index * width, animated });
    },
    [columns.length, width],
  );

  // While the user has not moved, a phase that moves on brings its page along.
  useEffect(() => {
    if (chosen === undefined) goTo(current, true);
  }, [chosen, current, goTo]);

  const onLayout = useCallback(
    (event: LayoutChangeEvent) => {
      const next = Math.round(event.nativeEvent.layout.width);
      if (next > 0 && next !== width) setWidth(next);
    },
    [width],
  );
  // A new width moves every page; the one on screen stays on screen.
  useEffect(() => {
    goTo(pageRef.current, false);
  }, [goTo]);

  const onSettled = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      const landed = pageAtOffset(event.nativeEvent.contentOffset.x, width, columns.length);
      if (landed !== pageRef.current) setChosen(chosenAfterMove(columns, landed));
    },
    [columns, width],
  );

  const renderPage = useCallback(
    ({ item, index }: { item: StructureColumn; index: number }) => (
      <PhasePage
        column={item}
        index={index}
        width={width}
        omitted={omitted}
        visible={index === page}
        contentContainerStyle={list.contentContainerStyle}
        attachAs={list.attachAs}
        follow={list.follow}
      />
    ),
    [list.attachAs, list.contentContainerStyle, list.follow, omitted, page, width],
  );

  return (
    <FooterContext.Provider value={footer}>
      <View onLayout={onLayout} style={styles.pager}>
        <View style={styles.pagerHead}>
          <Rail
            chips={chips}
            scrolls
            selected={page}
            onSelect={(index) => {
              setChosen(chosenAfterMove(columns, index));
              goTo(index, true);
            }}
          />
          <Text
            accessibilityLabel={pageAccessibility(columns, page)}
            accessibilityLiveRegion="polite"
            style={styles.pageLabel}
          >
            {pageLabel(columns, page)}
          </Text>
        </View>
        <FlatList
          ref={pager}
          horizontal
          pagingEnabled
          data={columns}
          keyExtractor={columnKey}
          extraData={pagesState}
          initialScrollIndex={page}
          getItemLayout={(_data, index) => ({ length: width, offset: width * index, index })}
          onMomentumScrollEnd={onSettled}
          showsHorizontalScrollIndicator={false}
          keyboardShouldPersistTaps="handled"
          renderItem={renderPage}
        />
      </View>
    </FooterContext.Provider>
  );
}

function columnKey(column: StructureColumn): string {
  return column.id;
}

/** "seed 단계 화면, 6개 중 3번째", read out with the page label. */
function pageAccessibility(columns: readonly StructureColumn[], index: number): string {
  const column = columns[index];
  if (!column) return '';
  return t('phone.structure.pageAccessibility', { name: column.title, index: index + 1, count: columns.length });
}

/**
 * The 구조 body of the session screen. Without a structure it says why — an older Mac,
 * a newer payload than this app reads, still loading, or simply no request yet — inside
 * the same scrolling list, so tall cards below it scroll and pulling still refreshes.
 */
export function MightyStructureView({
  state,
  hasRuns,
  ...list
}: ListProps & { state: StructureState; hasRuns: boolean }) {
  const notice = structureNotice(state, hasRuns);
  if (state.kind === 'ok' && notice === undefined) {
    const { structure } = state;
    if (structure.shape === 'phases') {
      // A different set of phases (another style) starts the pager afresh.
      const key = structure.columns.map((column) => column.id).join('|');
      return <PhasePager key={key} columns={structure.columns} omitted={structure.omitted} {...list} />;
    }
    return <FreeList rows={structure.rows} omitted={structure.omitted} {...list} />;
  }
  return (
    <FlatList
      {...list.follow}
      ref={list.attachAs('notice')}
      data={NO_ROWS}
      renderItem={null}
      contentContainerStyle={list.contentContainerStyle}
      keyboardShouldPersistTaps="handled"
      ListHeaderComponent={list.header}
      ListHeaderComponentStyle={list.headerStyle}
      ListEmptyComponent={
        notice === 'needsAppUpdate' ? (
          <EmptyState
            title={t('phone.structure.unreadable.title')}
            description={t('phone.structure.unreadable.description')}
          />
        ) : notice === 'needsMacUpdate' ? (
          <EmptyState
            title={t('phone.structure.needsUpdate.title')}
            description={t('phone.structure.needsUpdate.description')}
          />
        ) : notice === 'loading' ? (
          <EmptyState title={t('phone.workspaces.loading')} />
        ) : (
          <EmptyState title={t('phone.blocks.empty.title')} description={t('phone.blocks.empty.description')} />
        )
      }
      ListFooterComponent={list.footer}
    />
  );
}

const NO_ROWS: readonly StructureRow[] = [];

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    pager: { flex: 1 },
    pagerHead: { gap: spacing.sm, paddingBottom: spacing.md, paddingHorizontal: spacing.lg, paddingTop: spacing.md },
    pageLabel: { color: palette.textMuted, fontSize: 12, fontWeight: '700' },
    railRow: { alignItems: 'center', flexDirection: 'row' },
    railStep: { alignItems: 'center', flexDirection: 'row' },
    railLine: { height: 2, marginHorizontal: spacing.sm, width: spacing.xl },
    chip: {
      alignItems: 'center',
      borderColor: 'transparent',
      borderRadius: radius.round,
      borderWidth: 1.5,
      flexDirection: 'row',
      gap: spacing.sm,
      maxWidth: 160,
      minHeight: 28,
      paddingHorizontal: spacing.lg,
    },
    chipMark: { fontSize: 10, fontWeight: '800' },
    chipTitle: { flexShrink: 1, fontSize: 12, fontWeight: '700' },
    listItem: { paddingBottom: spacing.sm },
    card: {
      ...cardShadow,
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.card,
      gap: spacing.md,
      paddingHorizontal: spacing.xl,
      paddingVertical: spacing.lg,
    },
    heading: { ...typeScale.heading, color: palette.text, fontSize: 15, lineHeight: 20 },
    part: {
      backgroundColor: palette.surface,
      borderRadius: radius.md,
      gap: spacing.sm,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md,
    },
    partHead: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
    partLabel: { color: palette.textFaint, fontSize: 11, fontWeight: '700' },
    tally: { color: palette.textMuted, flexShrink: 1, fontSize: 11.5, fontWeight: '600' },
    foldable: { gap: spacing.xs },
    bodyText: { color: palette.text, fontSize: 13, lineHeight: 19 },
    mutedText: { color: palette.textMuted, fontSize: 12, lineHeight: 17 },
    activityLine: { ...monoText, fontSize: 11.5, lineHeight: 16 },
    toggle: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    note: { color: palette.textFaint, fontSize: 11.5, fontWeight: '600', paddingVertical: spacing.sm },
    caption: { color: palette.textFaint, fontSize: 10.5, fontWeight: '600' },
    clusters: { gap: spacing.sm, paddingLeft: spacing.lg },
    cluster: {
      borderColor: palette.border,
      borderRadius: radius.md,
      borderStyle: 'dashed',
      borderWidth: 1,
      gap: spacing.sm,
      padding: spacing.md,
    },
    node: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: 1,
      flexDirection: 'row',
      overflow: 'hidden',
    },
    nodeBar: { width: 3 },
    nodeBody: { flex: 1, gap: spacing.xs, paddingHorizontal: spacing.md, paddingVertical: spacing.sm },
    nodeTitle: { color: palette.text, fontSize: 12.5, fontWeight: '700' },
    nodeMeta: { color: palette.textMuted, fontSize: 11 },
    pressed: { opacity: 0.6 },
  });
