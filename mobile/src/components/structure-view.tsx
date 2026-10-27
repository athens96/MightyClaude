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
  Animated,
  Easing,
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
import type { MobileStructure, StructureActivity, StructureItem, StructureNode, StructureRow } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { Sheet } from '@/components/sheets';
import { StatusChip } from '@/components/status-chip';
import { Button, EmptyState } from '@/components/ui';
import type { EndScrollable, FollowBottomProps } from '@/hooks/use-follow-bottom';
import { t } from '@/lib/i18n';
import { blockKindLabel } from '@/lib/mighty';
import {
  NEWEST_BOARD,
  boardColumnAccessibility,
  boardCurrentStep,
  boardIndex,
  boardPosition,
  boardStepName,
  boardStepsSummary,
  boardTitle,
  cardTone,
  chosenColumnAfterMove,
  chosenColumnAfterUpdate,
  columnTone,
  hasUnseenBoard,
  onNewestBoard,
  selectBoard,
  stepBoard,
  structureBoards,
  visibleColumn,
  type Board,
  type BoardCard,
  type BoardColumn,
  type BoardNavigation,
} from '@/lib/pipeline-board';
import { toneOf, type Tone } from '@/lib/status-tone';
import { submitScroll } from '@/lib/submit-scroll';
import {
  FOLD_LINES,
  foldText,
  foldedRequestLine,
  foldedResultLine,
  isCardFolded,
  itemHeading,
  nodeFolds,
  nodeMeta,
  pageAtOffset,
  resultHeading,
  stepLook,
  structureNotice,
  type StepLook,
  type StructureState,
} from '@/lib/structure';
import {
  cardShadow,
  monoText,
  pipeline,
  radius,
  spacing,
  statusLabel,
  tintOver,
  toneColors,
  toneMark,
  typeScale,
  usePalette,
  useStyles,
  type Palette,
} from '@/theme';

/**
 * The 파이프라인 view: the Mac's structure of the pane as a kanban board, one board at a
 * time (`structureBoards`): one request of a pane without a style, or one pipeline of a
 * styled pane. ◀ ▶ and a list move between boards, and a badge says a newer one arrived
 * while an older one is read. Under the board's status, its steps are a row of chips with
 * their card counts joined by a line; the columns are pages swiped sideways, one column per
 * screen width, each listing its cards. Every step and phase comes from the Mac
 * (`MobileMighty.structure`), so the two screens always agree.
 */

/**
 * The colours one step takes: done green, current in its column's colour (`columnTone`:
 * running, waiting, or how its newest request ended), pending grey; an error or a stop in its own.
 */
function stepColors(palette: Palette, look: StepLook, tone: Tone | undefined): { fill: string; ink: string } {
  switch (look.tone) {
    case 'done':
      return { fill: toneColors(palette, 'done').fill, ink: toneColors(palette, 'done').onFill };
    case 'current': {
      const colors = toneColors(palette, tone ?? 'run');
      return { fill: colors.fill, ink: colors.onFill };
    }
    case 'err':
      return { fill: toneColors(palette, 'err').fill, ink: toneColors(palette, 'err').onFill };
    case 'stop':
      return { fill: toneColors(palette, 'stop').fill, ink: toneColors(palette, 'stop').onFill };
    default:
      return { fill: palette.track, ink: palette.textMuted };
  }
}

/**
 * A 파이프라인 card's state (`cardTone`): its tone's soft tint laid on the card's own surface;
 * nothing for a step still to come. `CardBar` draws the bar down its leading edge.
 */
function cardTint(palette: Palette, tone: Tone | undefined): ViewStyle | undefined {
  if (!tone) return undefined;
  return { backgroundColor: tintOver(palette.surfaceRaised, toneColors(palette, tone).soft, pipeline.cardTintOpacity) };
}

/**
 * A tinted card's bar in its tone's mark: a straight bar down the leading edge, clipped to the
 * card's corners by a layer of its own, so the card keeps its shadow, its content does not move
 * and the running outline still goes round the whole card. Nothing on an untinted card.
 */
function CardBar({ tone }: { tone: Tone | undefined }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  if (!tone) return null;
  return (
    <View pointerEvents="none" style={styles.cardClip}>
      <View style={[styles.cardBar, { backgroundColor: toneMark(palette, tone) }]} />
    </View>
  );
}

/**
 * A status pill on a pipeline card: on a tinted card, a hairline in the card's mark round it, so
 * the pill's own soft fill still reads against the tint (the Mac's `tintedCardEdge`).
 */
function CardPill({ status, tone }: { status: string; tone: Tone | undefined }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View>
      <StatusChip status={status} />
      {tone ? <View pointerEvents="none" style={[styles.pillEdge, { borderColor: toneMark(palette, tone) }]} /> : null}
    </View>
  );
}

/**
 * The running mark around a card: a border in the running colour that breathes, as the
 * status chip's dot does. Driven natively, so a running card costs no re-render per frame.
 */
function RunningOutline({ status, corner }: { status: string; corner: number }) {
  const palette = usePalette();
  const pulse = useRef(new Animated.Value(1)).current;
  const running = status === 'running';
  useEffect(() => {
    if (!running) return undefined;
    const animation = Animated.loop(
      Animated.sequence([
        Animated.timing(pulse, { toValue: 0.25, duration: 650, easing: Easing.inOut(Easing.quad), useNativeDriver: true }),
        Animated.timing(pulse, { toValue: 1, duration: 650, easing: Easing.inOut(Easing.quad), useNativeDriver: true }),
      ]),
    );
    animation.start();
    return () => animation.stop();
  }, [pulse, running]);
  if (!running) return null;
  return (
    <Animated.View
      pointerEvents="none"
      style={[
        StyleSheet.absoluteFill,
        { borderColor: toneColors(palette, 'run').fill, borderRadius: corner, borderWidth: 1.5, opacity: pulse },
      ]}
    />
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

function Tally({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  return (
    <Text numberOfLines={1} style={styles.tally}>
      {t('graph.structure.tally', { agents: item.tally.agents, tools: item.tally.tools })}
    </Text>
  );
}

/** Sub-agent cards the Mac left out to fit the phone's budget, counted under the request's 실행. */
function OmittedNodes({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  return item.omittedNodes ? (
    <Text style={styles.note}>{t('phone.structure.nodesOmitted', { count: item.omittedNodes })}</Text>
  ) : null;
}

/** The main agent's latest reply and its last tool calls, or that it has not answered yet. */
function Progress({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  return (
    <>
      {item.message ? (
        <FoldableText text={item.message} lines={FOLD_LINES.message} label={t('graph.structure.latest')} />
      ) : !item.finished && item.activity.length === 0 ? (
        <Text style={styles.mutedText}>{t('graph.block.waitingResponse')}</Text>
      ) : null}
      {item.activity.length > 0 ? (
        <View accessibilityLabel={t('graph.structure.activity')}>
          {item.activity.map((line, index) => (
            <ActivityLine key={`${index}:${line.text}`} line={line} />
          ))}
        </View>
      ) : null}
    </>
  );
}

/** The answer as Markdown; without one, how the request ended or that it has not yet. */
function Outcome({ item }: { item: StructureItem }) {
  const styles = useStyles(makeStyles);
  if (item.answer) {
    return <FoldableText markdown text={item.answer} lines={FOLD_LINES.answer} label={resultHeading(item)} />;
  }
  return (
    <Text style={styles.mutedText}>{item.finished ? statusLabel(item.status) : t('graph.structure.resultPending')}</Text>
  );
}

/** A free board's 요청: the column and the bar already say which, so the card is what was asked. */
const RequestCard = memo(function RequestCard({ item, tone }: { item: StructureItem; tone?: Tone }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={[styles.card, cardTint(palette, tone)]}>
      <CardBar tone={tone} />
      {item.request ? (
        <FoldableText text={item.request} lines={FOLD_LINES.request} label={t('graph.structure.column.request')} />
      ) : (
        <Text style={styles.heading}>{itemHeading(item)}</Text>
      )}
    </View>
  );
});

/** A free board's 실행: it starts with its status, then the latest reply and tool calls. */
const ExecutionCard = memo(function ExecutionCard({ item, tone }: { item: StructureItem; tone?: Tone }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View accessibilityLabel={t('graph.block.executionOf', { n: item.ordinal })} style={[styles.card, cardTint(palette, tone)]}>
      <CardBar tone={tone} />
      <View style={styles.partHead}>
        <CardPill status={item.status} tone={tone} />
        <Tally item={item} />
      </View>
      <Progress item={item} />
      <OmittedNodes item={item} />
      <RunningOutline status={item.status} corner={radius.card} />
    </View>
  );
});

/** A free board's 결과: titled only when the request failed or stopped. */
const ResultCard = memo(function ResultCard({ item, tone }: { item: StructureItem; tone?: Tone }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const ended = item.status === 'error' || item.status === 'stopped';
  return (
    <View style={[styles.card, cardTint(palette, tone)]}>
      <CardBar tone={tone} />
      {ended ? (
        <Text style={[styles.partLabel, { color: toneColors(palette, toneOf(item.status)).ink }]}>{resultHeading(item)}</Text>
      ) : null}
      <Outcome item={item} />
    </View>
  );
});

/**
 * Which finished requests the user opened, and how they open or fold one, from the
 * screen's state: the cards read it through context, so the lists never see a new prop.
 */
interface Folds {
  unfolded: ReadonlySet<string>;
  onToggle: (item: StructureItem) => void;
}
const FoldContext = createContext<Folds>({ unfolded: new Set(), onToggle: () => undefined });

/**
 * A finished request of a pipeline folded to one compact card: its heading, status and
 * tally, what was asked and how it ended, a line each. Tapping it opens the request.
 */
function FoldedCard({ item, onOpen, tone }: { item: StructureItem; onOpen: () => void; tone?: Tone }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const answer = foldedResultLine(item);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${itemHeading(item)}, ${statusLabel(item.status)}`}
      accessibilityHint={t('graph.structure.unfoldCard')}
      accessibilityState={{ expanded: false }}
      onPress={onOpen}
      style={({ pressed }) => [styles.card, cardTint(palette, tone), styles.folded, pressed && styles.pressed]}
    >
      <CardBar tone={tone} />
      <View style={styles.foldedHead}>
        <Text numberOfLines={1} style={[styles.heading, styles.foldedHeading]}>
          {itemHeading(item)}
        </Text>
        <CardPill status={item.status} tone={tone} />
        <Tally item={item} />
        <Text style={styles.foldMark}>▾</Text>
      </View>
      <Text numberOfLines={1} style={styles.bodyText}>
        {foldedRequestLine(item)}
      </Text>
      <Text
        numberOfLines={1}
        style={[styles.mutedText, answer === undefined && { color: toneColors(palette, toneOf(item.status)).ink }]}
      >
        {answer ?? statusLabel(item.status)}
      </Text>
    </Pressable>
  );
}

/**
 * A pipeline's request: its heading, what was asked, its 실행 line (status and tally) with
 * the latest reply and tool calls, and once it finished its result. A finished request
 * folds to `FoldedCard` until the user opens it.
 */
const SummaryCard = memo(function SummaryCard({
  item,
  folded,
  onToggle,
  tone,
}: {
  item: StructureItem;
  folded: boolean;
  onToggle: (item: StructureItem) => void;
  tone?: Tone;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  if (folded) return <FoldedCard item={item} tone={tone} onOpen={() => onToggle(item)} />;
  return (
    <View style={[styles.card, cardTint(palette, tone)]}>
      <CardBar tone={tone} />
      <View style={styles.headingRow}>
        <Text accessibilityRole="header" numberOfLines={2} style={[styles.heading, styles.headingText]}>
          {itemHeading(item)}
        </Text>
        {item.finished ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('graph.structure.foldCard')}
            accessibilityState={{ expanded: true }}
            hitSlop={8}
            onPress={() => onToggle(item)}
            style={({ pressed }) => pressed && styles.pressed}
          >
            <Text style={styles.foldMark}>▴</Text>
          </Pressable>
        ) : null}
      </View>
      {item.request ? (
        <FoldableText text={item.request} lines={FOLD_LINES.request} label={t('graph.structure.column.request')} />
      ) : null}
      <View style={styles.divider} />
      <View style={styles.partHead}>
        <Text numberOfLines={1} style={styles.partLabel}>
          {t('graph.block.executionOf', { n: item.ordinal })}
        </Text>
        <CardPill status={item.status} tone={tone} />
        <Tally item={item} />
      </View>
      <Progress item={item} />
      <OmittedNodes item={item} />
      {item.finished ? (
        <View style={styles.foldable}>
          <Text style={[styles.partLabel, { color: toneColors(palette, toneOf(item.status)).ink }]}>
            {resultHeading(item)}
          </Text>
          <Outcome item={item} />
        </View>
      ) : null}
      <RunningOutline status={item.status} corner={radius.card} />
    </View>
  );
});

/**
 * A sub-agent or task: its status as a bar, its title, kind and model, the block it runs
 * under when nested (indented by its depth too), what it was asked and said.
 */
const AgentCard = memo(function AgentCard({ node, parent, tone }: { node: StructureNode; parent?: string; tone?: Tone }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [open, setOpen] = useState(false);
  const folds = nodeFolds(node);
  const title = node.title || blockKindLabel(node.kind);
  const prompt = node.prompt ? (open ? node.prompt : foldText(node.prompt, FOLD_LINES.prompt).preview) : undefined;
  const output = node.output ? (open ? node.output : foldText(node.output, FOLD_LINES.output).preview) : undefined;
  return (
    <View
      style={[
        styles.node,
        { marginLeft: node.depth * spacing.lg },
        tone && { backgroundColor: tintOver(palette.surface, toneColors(palette, tone).soft, pipeline.cardTintOpacity) },
      ]}
    >
      {/* The sub-agent card's own bar is the pipeline's bar: one bar, in the tone's mark; none untinted. */}
      {tone ? <View style={[styles.nodeBar, { backgroundColor: toneMark(palette, tone) }]} /> : null}
      <View style={styles.nodeBody}>
        <Text
          accessibilityLabel={t('graph.structure.nodeAccessibility', { title, status: statusLabel(node.status) })}
          numberOfLines={1}
          style={styles.nodeTitle}
        >
          {title}
        </Text>
        <Text numberOfLines={1} style={styles.nodeMeta}>
          {`${nodeMeta(node, blockKindLabel)} · ${statusLabel(node.status)}`}
        </Text>
        {parent !== undefined ? (
          <Text numberOfLines={1} style={styles.nodeMeta}>
            {`↳ ${t('graph.structure.underParent', { title: parent })}`}
          </Text>
        ) : null}
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
      <RunningOutline status={node.status} corner={radius.sm} />
    </View>
  );
});

function CardCell({ card, tone }: { card: BoardCard; tone: Tone | undefined }) {
  const styles = useStyles(makeStyles);
  const { unfolded, onToggle } = useContext(FoldContext);
  let body: ReactElement;
  switch (card.kind) {
    case 'request':
      body = <RequestCard item={card.item} tone={tone} />;
      break;
    case 'execution':
      body = <ExecutionCard item={card.item} tone={tone} />;
      break;
    case 'result':
      body = <ResultCard item={card.item} tone={tone} />;
      break;
    case 'summary':
      body = <SummaryCard item={card.item} folded={isCardFolded(card.item, unfolded)} onToggle={onToggle} tone={tone} />;
      break;
    default:
      body = <AgentCard node={card.node} parent={card.parent} tone={tone} />;
  }
  return <View style={styles.listItem}>{body}</View>;
}

function cardKey(card: BoardCard): string {
  return card.id;
}

/** A column header chip: the step's mark and name in its state's colours, and its card count. */
function ColumnChip({ column, selected, onPress }: { column: BoardColumn; selected: boolean; onPress: () => void }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const colors = stepColors(palette, stepLook(column.step.step), columnTone(column));
  return (
    <Pressable
      accessibilityRole="tab"
      accessibilityLabel={boardColumnAccessibility(column)}
      accessibilityHint={t('phone.structure.chipHint')}
      accessibilityState={{ selected }}
      hitSlop={{ bottom: 6, top: 6 }}
      onPress={onPress}
      style={({ pressed }) => pressed && styles.pressed}
    >
      <View style={[styles.chip, { backgroundColor: colors.fill }, selected && { borderColor: palette.text }]}>
        <Text style={[styles.chipMark, { color: colors.ink }]}>{stepLook(column.step.step).mark}</Text>
        <Text numberOfLines={1} style={[styles.chipTitle, { color: colors.ink }]}>
          {boardStepName(column.step)}
        </Text>
        <Text style={[styles.chipCount, { color: colors.ink }]}>{column.count}</Text>
      </View>
    </Pressable>
  );
}

/** The board's pipeline: its column chips joined by a line that turns green once a step is done. */
function ColumnRail({
  columns,
  selected,
  onSelect,
}: {
  columns: readonly BoardColumn[];
  selected: number;
  onSelect: (index: number) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const scroller = useRef<ScrollView>(null);
  /** Where each chip starts along the rail, so the selected one can be brought into view. */
  const starts = useRef(new Map<number, number>());
  useEffect(() => {
    const x = starts.current.get(selected);
    if (x !== undefined) scroller.current?.scrollTo({ x: Math.max(0, x - spacing.xl), animated: true });
  }, [selected]);
  return (
    <ScrollView
      ref={scroller}
      horizontal
      accessibilityLabel={t('guidedPanel.phasesAccessibility')}
      accessibilityRole="tablist"
      contentContainerStyle={styles.railRow}
      showsHorizontalScrollIndicator={false}
    >
      {columns.map((column, index) => (
        <View
          key={column.step.id}
          onLayout={(event) => {
            starts.current.set(index, event.nativeEvent.layout.x);
            if (index === selected) {
              scroller.current?.scrollTo({ x: Math.max(0, event.nativeEvent.layout.x - spacing.xl), animated: false });
            }
          }}
          style={styles.railStep}
        >
          <ColumnChip column={column} selected={index === selected} onPress={() => onSelect(index)} />
          {index < columns.length - 1 ? (
            <View
              style={[
                styles.railLine,
                { backgroundColor: column.step.step === 'done' ? toneColors(palette, 'done').fill : palette.track },
              ]}
            />
          ) : null}
        </View>
      ))}
    </ScrollView>
  );
}

interface ListProps {
  header?: ReactElement | null;
  footer?: ReactElement | null;
  contentContainerStyle?: StyleProp<ViewStyle>;
  headerStyle?: StyleProp<ViewStyle>;
  /** Attaches the vertical list on screen, under its own key (`useFollowBottom().attachAs`). */
  attachAs: (key: string) => (list: EndScrollable | null) => void;
  follow?: FollowBottomProps;
  /**
   * Bumped on each own send (natural submit B2): an older board the user chose goes back to
   * the newest, and the newest board's pager to its current step, the new request's column.
   */
  ownSend?: number;
}

/**
 * The screen's footer (permission cards, the queue), handed down through context so a
 * new footer element — the screen renders one on every keystroke — redraws only this
 * slot and never the pages or the cards.
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

/** One column as a page of the board: its cards, top to bottom. */
const ColumnPage = memo(function ColumnPage({
  board,
  column,
  index,
  width,
  visible,
  contentContainerStyle,
  attachAs,
  follow,
}: {
  board: string;
  column: BoardColumn;
  index: number;
  width: number;
  visible: boolean;
  contentContainerStyle?: StyleProp<ViewStyle>;
  attachAs: ListProps['attachAs'];
  follow?: FollowBottomProps;
}) {
  const styles = useStyles(makeStyles);
  const palette = usePalette();
  // The step's state as a wash behind its cards; none while it is still to come (`columnTone`).
  const tone = columnTone(column);
  const renderItem = useCallback(
    ({ item }: { item: BoardCard }) => <CardCell card={item} tone={cardTone(item, column)} />,
    [column],
  );
  return (
    <View
      style={[
        { width },
        tone && { backgroundColor: tintOver(palette.background, toneColors(palette, tone).soft, pipeline.columnTintOpacity) },
      ]}
    >
      <FlatList
        {...(visible ? follow : undefined)}
        ref={visible ? attachAs(`column:${board}:${column.step.id}`) : undefined}
        data={column.cards}
        keyExtractor={cardKey}
        renderItem={renderItem}
        contentContainerStyle={contentContainerStyle}
        keyboardShouldPersistTaps="handled"
        nestedScrollEnabled
        ListEmptyComponent={<Text style={styles.note}>{t('phone.structure.emptyPhase')}</Text>}
        ListFooterComponent={pageFooter(index)}
      />
    </View>
  );
});

/** "실행 단계 화면, 3개 중 2번째", read out as the page changes. */
function pageAccessibility(board: Board, index: number): string {
  const step = board.steps[index];
  if (!step) return '';
  return t('phone.structure.pageAccessibility', { name: boardStepName(step), index: index + 1, count: board.steps.length });
}

/**
 * One board's columns as pages swiped sideways under its pipeline chips. The view opens on
 * the board's current step and follows it until the user moves to another column.
 */
function ColumnPager({ board, ...list }: ListProps & { board: Board }) {
  const styles = useStyles(makeStyles);
  const screen = useWindowDimensions();
  const [width, setWidth] = useState(screen.width);
  /** The column the user swiped or tapped to, by step id; none while the pager follows the current step. */
  const [chosen, setChosen] = useState<string | undefined>(undefined);
  const pager = useRef<FlatList<BoardColumn>>(null);
  const page = visibleColumn(board, chosen);
  const pageRef = useRef(page);
  pageRef.current = page;
  const current = boardCurrentStep(board);
  const pagesState = useMemo(() => ({ page, width }), [page, width]);
  const footer = useMemo(() => ({ footer: list.footer, page }), [list.footer, page]);

  const goTo = useCallback(
    (index: number, animated: boolean) => {
      if (index < 0 || index >= board.columns.length) return;
      pager.current?.scrollToOffset({ offset: index * width, animated });
    },
    [board.columns.length, width],
  );

  // An own send drops the column the user picked, so the pager follows the current step.
  const ownSend = useRef(list.ownSend);
  useEffect(() => {
    if (ownSend.current === list.ownSend) return;
    ownSend.current = list.ownSend;
    setChosen(undefined);
  }, [list.ownSend]);

  // While the user has not moved, a step that moves on brings its column along; once the
  // current step reaches the column the user picked, the pick is dropped and following resumes.
  useEffect(() => {
    if (chosen === undefined) goTo(current, true);
    else if (chosenColumnAfterUpdate(board, chosen) === undefined) setChosen(undefined);
  }, [board, chosen, current, goTo]);

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
      const landed = pageAtOffset(event.nativeEvent.contentOffset.x, width, board.columns.length);
      if (landed !== pageRef.current) setChosen(chosenColumnAfterMove(board, landed));
    },
    [board, width],
  );

  const renderPage = useCallback(
    ({ item, index }: { item: BoardColumn; index: number }) => (
      <ColumnPage
        board={board.id}
        column={item}
        index={index}
        width={width}
        visible={index === page}
        contentContainerStyle={list.contentContainerStyle}
        attachAs={list.attachAs}
        follow={list.follow}
      />
    ),
    [board.id, list.attachAs, list.contentContainerStyle, list.follow, page, width],
  );

  return (
    <FooterContext.Provider value={footer}>
      <View onLayout={onLayout} style={styles.pager}>
        <View style={styles.railBox}>
          <ColumnRail
            columns={board.columns}
            selected={page}
            onSelect={(index) => {
              setChosen(chosenColumnAfterMove(board, index));
              goTo(index, true);
            }}
          />
          <Text accessibilityLiveRegion="polite" style={styles.srOnly}>
            {pageAccessibility(board, page)}
          </Text>
        </View>
        <FlatList
          ref={pager}
          horizontal
          pagingEnabled
          data={board.columns}
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

function columnKey(column: BoardColumn): string {
  return column.step.id;
}

function NavButton({ mark, label, disabled, onPress }: { mark: string; label: string; disabled: boolean; onPress: () => void }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled }}
      disabled={disabled}
      hitSlop={6}
      onPress={onPress}
      style={({ pressed }) => [styles.navButton, pressed && styles.pressed]}
    >
      <Text style={[styles.navMark, { color: disabled ? palette.textFaint : palette.text }]}>{mark}</Text>
    </Pressable>
  );
}

/** Every board, newest last: its number, status, title and steps. Tapping one shows it. */
function BoardListSheet({
  visible,
  boards,
  shown,
  omitted,
  onSelect,
  onClose,
}: {
  visible: boolean;
  boards: readonly Board[];
  shown: number;
  omitted?: number;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const rows = useMemo(
    () =>
      boards.map((board, index) => ({
        board,
        title: boardTitle(board, boards.length, omitted),
        steps: boardStepsSummary(board),
        position: boardPosition(board, index, boards.length, omitted),
      })),
    [boards, omitted],
  );
  if (!visible) return null;
  return (
    <Sheet visible onClose={onClose}>
      <Text style={styles.sheetTitle}>{t('graph.structure.board.list')}</Text>
      {omitted ? <Text style={styles.note}>{t('phone.structure.omitted', { count: omitted })}</Text> : null}
      <ScrollView style={styles.boardList}>
        {rows.map(({ board, title, steps, position }, index) => {
          const selected = index === shown;
          return (
            <Pressable
              key={board.id}
              accessibilityRole="button"
              accessibilityLabel={`${position}, ${title}`}
              accessibilityValue={{ text: `${statusLabel(board.status)}, ${steps}` }}
              accessibilityState={{ selected }}
              onPress={() => onSelect(board.id)}
              style={({ pressed }) => [
                styles.boardRow,
                selected && { backgroundColor: palette.accentMuted },
                pressed && styles.pressed,
              ]}
            >
              <Text style={styles.boardNumber}>{board.kind === 'request' ? (omitted ?? 0) + index + 1 : index + 1}</Text>
              <View style={styles.boardTexts}>
                <View style={styles.partHead}>
                  <StatusChip status={board.status} />
                  <Text numberOfLines={1} style={[styles.boardTitle, selected && { color: palette.accent }]}>
                    {title}
                  </Text>
                </View>
                <Text numberOfLines={1} style={styles.mutedText}>
                  {steps}
                </Text>
              </View>
            </Pressable>
          );
        })}
      </ScrollView>
      <Button label={t('common.close')} tone="ghost" onPress={onClose} />
    </Sheet>
  );
}

/**
 * The boards one at a time: ◀, the board's place (which opens the list), ▶ and a badge for
 * a newer board; under them the board's status, with a pipeline's name (a request's first
 * line is its 요청 card already); then its columns.
 */
function BoardView({
  structure,
  ...list
}: ListProps & { structure: MobileStructure }) {
  const styles = useStyles(makeStyles);
  const palette = usePalette();
  const boards = useMemo(() => structureBoards(structure), [structure]);
  const [navigation, setNavigation] = useState<BoardNavigation>(NEWEST_BOARD);
  const [listOpen, setListOpen] = useState(false);
  const index = boardIndex(navigation, boards);
  const board = index === undefined ? undefined : boards[index];
  // An own send goes back to the newest board from an older one (pending.json scrollTargets).
  const ownSend = useRef(list.ownSend);
  useEffect(() => {
    if (ownSend.current === list.ownSend) return;
    ownSend.current = list.ownSend;
    if (submitScroll('pipeline', true, onNewestBoard(navigation, boards), false).resetBoard) setNavigation(NEWEST_BOARD);
  }, [boards, list.ownSend, navigation]);
  if (index === undefined || !board) {
    return (
      <FlatList
        {...list.follow}
        ref={list.attachAs('notice')}
        data={NO_ROWS}
        renderItem={null}
        contentContainerStyle={list.contentContainerStyle}
        ListEmptyComponent={
          <EmptyState title={t('phone.blocks.empty.title')} description={t('phone.blocks.empty.description')} />
        }
        ListFooterComponent={list.footer}
      />
    );
  }
  const position = boardPosition(board, index, boards.length, structure.omitted);
  const unseen = hasUnseenBoard(navigation, boards);
  const newer = board.kind === 'request' ? t('graph.structure.board.newRequest') : t('graph.structure.board.newPipeline');
  return (
    <View style={styles.pager}>
      <View style={styles.boardHead}>
        <View style={styles.navRow}>
          <NavButton
            mark="◀"
            label={t('graph.structure.board.previous')}
            disabled={index === 0}
            onPress={() => setNavigation((value) => stepBoard(value, -1, boards))}
          />
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('graph.structure.board.list')}
            accessibilityValue={{ text: position }}
            hitSlop={6}
            onPress={() => setListOpen(true)}
            style={({ pressed }) => [styles.positionButton, pressed && styles.pressed]}
          >
            <Text numberOfLines={1} style={styles.position}>
              {position}
            </Text>
            <Text style={styles.foldMark}>▾</Text>
          </Pressable>
          <NavButton
            mark="▶"
            label={t('graph.structure.board.next')}
            disabled={index === boards.length - 1}
            onPress={() => setNavigation((value) => stepBoard(value, 1, boards))}
          />
          {unseen ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={newer}
              accessibilityHint={t('graph.structure.board.showNewest')}
              hitSlop={6}
              onPress={() => setNavigation(NEWEST_BOARD)}
              style={({ pressed }) => [styles.newerBadge, { backgroundColor: palette.accent }, pressed && styles.pressed]}
            >
              <Text numberOfLines={1} style={[styles.newerText, { color: palette.onAccent }]}>
                {newer}
              </Text>
            </Pressable>
          ) : null}
        </View>
        <View style={styles.statusRow}>
          <StatusChip status={board.status} />
          {board.kind === 'pipeline' ? (
            <Text accessibilityRole="header" numberOfLines={1} style={[styles.heading, styles.headingText]}>
              {boardTitle(board, boards.length, structure.omitted)}
            </Text>
          ) : null}
        </View>
        {index === 0 && structure.omitted ? (
          <Text style={styles.note}>{t('phone.structure.omitted', { count: structure.omitted })}</Text>
        ) : null}
      </View>
      <ColumnPager key={board.id} board={board} {...list} />
      <BoardListSheet
        visible={listOpen}
        boards={boards}
        shown={index}
        omitted={structure.omitted}
        onSelect={(id) => {
          setNavigation((value) => selectBoard(value, id, boards));
          setListOpen(false);
        }}
        onClose={() => setListOpen(false)}
      />
    </View>
  );
}

/**
 * The 파이프라인 body of the session screen. Without a structure it says why — an older
 * Mac, a newer payload than this app reads, still loading, or simply no request yet —
 * inside the same scrolling list, so pulling still refreshes.
 */
export function MightyStructureView({
  state,
  hasRuns,
  unfolded,
  onToggleFold,
  ...list
}: ListProps & {
  state: StructureState;
  hasRuns: boolean;
  /** Finished requests the user opened, by run id (`isCardFolded`), kept by the screen. */
  unfolded: ReadonlySet<string>;
  onToggleFold: (item: StructureItem) => void;
}) {
  const folds = useMemo(() => ({ unfolded, onToggle: onToggleFold }), [unfolded, onToggleFold]);
  const notice = structureNotice(state, hasRuns);
  if (state.kind === 'ok' && notice === undefined) {
    return (
      <FoldContext.Provider value={folds}>
        <BoardView structure={state.structure} {...list} />
      </FoldContext.Provider>
    );
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
    boardHead: { gap: spacing.sm, paddingHorizontal: spacing.lg, paddingTop: spacing.md },
    navRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    navButton: { alignItems: 'center', height: 32, justifyContent: 'center', width: 32 },
    navMark: { fontSize: 13, fontWeight: '700' },
    positionButton: { alignItems: 'center', flexDirection: 'row', flexShrink: 1, gap: spacing.sm, minHeight: 32 },
    position: { color: palette.text, fontSize: 13, fontVariant: ['tabular-nums'], fontWeight: '700' },
    newerBadge: { borderRadius: radius.round, paddingHorizontal: spacing.lg, paddingVertical: spacing.xs },
    newerText: { fontSize: 11, fontWeight: '800' },
    statusRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.md },
    railBox: { paddingBottom: spacing.md, paddingHorizontal: spacing.lg, paddingTop: spacing.md },
    srOnly: { height: 0, opacity: 0, position: 'absolute', width: 0 },
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
      maxWidth: 180,
      minHeight: 28,
      paddingHorizontal: spacing.lg,
    },
    chipMark: { fontSize: 10, fontWeight: '800' },
    chipTitle: { flexShrink: 1, fontSize: 12, fontWeight: '700' },
    chipCount: { fontSize: 11, fontVariant: ['tabular-nums'], fontWeight: '600', opacity: 0.85 },
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
    headingRow: { alignItems: 'flex-start', flexDirection: 'row', gap: spacing.md },
    headingText: { flex: 1 },
    cardClip: { bottom: 0, left: 0, position: 'absolute', right: 0, top: 0, borderRadius: radius.card, overflow: 'hidden' },
    cardBar: { bottom: 0, left: 0, position: 'absolute', top: 0, width: pipeline.cardBarWidth },
    pillEdge: { bottom: 0, left: 0, position: 'absolute', right: 0, top: 0, borderRadius: radius.round, borderWidth: 1 },
    folded: { gap: spacing.xs },
    foldedHead: { alignItems: 'center', flexDirection: 'row', gap: spacing.md },
    foldedHeading: { flexShrink: 1 },
    foldMark: { color: palette.textMuted, fontSize: 14, fontWeight: '700' },
    divider: { backgroundColor: palette.border, height: StyleSheet.hairlineWidth },
    partHead: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.md },
    partLabel: { color: palette.textFaint, fontSize: 11, fontWeight: '700' },
    tally: { color: palette.textMuted, flexShrink: 1, fontSize: 11.5, fontWeight: '600' },
    foldable: { gap: spacing.xs },
    bodyText: { color: palette.text, fontSize: 13, lineHeight: 19 },
    mutedText: { color: palette.textMuted, fontSize: 12, lineHeight: 17 },
    activityLine: { ...monoText, fontSize: 11.5, lineHeight: 16 },
    toggle: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    note: { color: palette.textFaint, fontSize: 11.5, fontWeight: '600', paddingVertical: spacing.sm },
    node: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: 1,
      flexDirection: 'row',
      overflow: 'hidden',
    },
    nodeBar: { width: pipeline.cardBarWidth },
    nodeBody: { flex: 1, gap: spacing.xs, paddingHorizontal: spacing.md, paddingVertical: spacing.sm },
    nodeTitle: { color: palette.text, fontSize: 12.5, fontWeight: '700' },
    nodeMeta: { color: palette.textMuted, fontSize: 11 },
    sheetTitle: { ...typeScale.title, color: palette.text },
    boardList: { maxHeight: 420 },
    boardRow: {
      alignItems: 'flex-start',
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.md,
      flexDirection: 'row',
      gap: spacing.md,
      marginBottom: spacing.xs,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.md,
    },
    boardNumber: { color: palette.textMuted, fontSize: 12, fontVariant: ['tabular-nums'], fontWeight: '700', minWidth: 18 },
    boardTexts: { flex: 1, gap: spacing.xs },
    boardTitle: { color: palette.text, flexShrink: 1, fontSize: 14, fontWeight: '600' },
    pressed: { opacity: 0.6 },
  });
