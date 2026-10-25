import type {
  MobileStructure,
  StructureColumn,
  StructureItem,
  StructureNode,
  StructureRow,
  StructureStep,
} from '@/api/types';
import { t } from '@/lib/i18n';
import { blockKindLabel } from '@/lib/mighty';
import { stepStateWord } from '@/lib/structure';
import { pipelineTone, stepTone, type Tone } from '@/lib/status-tone';

/**
 * The 파이프라인 view's boards: one request of a pane without a style, or one pipeline of a
 * styled pane, shown one at a time as a row of columns, one per step. The same rules as
 * the Mac's `MightyStructure.boards` (`native/macos/Sources/MightyCore/MightyStructureBoard.swift`),
 * read off the structure the Mac already sends (`mighty.structure`), so a phone and a Mac
 * that show the same requests group them the same way. Pure, so it is tested apart.
 */

export type BoardKind = 'request' | 'pipeline';

/** One step of a board: a free request's 요청, 실행 or 결과, or a phase. */
export interface BoardStep {
  /** "request", "execution" or "result" for a free board, else the phase id. */
  id: string;
  /** The phase's title; absent for a free board's steps, which `boardStepName` names. */
  title?: string;
  step: StructureStep;
}

/**
 * One card of a column. A free board has the request alone, its 실행 part and its result;
 * a styled board has one summary card per request; both list each sub-agent after the
 * request it runs under, naming the block it runs under (`parent`) when it is nested.
 */
export type BoardCard =
  | { kind: 'request' | 'execution' | 'result' | 'summary'; id: string; item: StructureItem }
  | { kind: 'agent'; id: string; node: StructureNode; runId: string; parent?: string };

export interface BoardColumn {
  step: BoardStep;
  cards: BoardCard[];
  /** The header's count: a styled column's requests, a free column's cards (결과 once there is one). */
  count: number;
}

export interface Board {
  /** The first request's run id. */
  id: string;
  kind: BoardKind;
  /** 1-based place among the boards the phone holds. */
  ordinal: number;
  /** The board's requests, oldest first. */
  items: StructureItem[];
  /** The first request's first line, when it has one. */
  headline?: string;
  /** Running or waiting while any request still is, else the newest request's. */
  status: string;
  steps: BoardStep[];
  /** One column per step, in the same order. */
  columns: BoardColumn[];
}

/** The boards of a structure, oldest first. */
export function structureBoards(structure: MobileStructure): Board[] {
  return structure.shape === 'free'
    ? structure.rows.map((row, index) => freeBoard(row, index + 1))
    : pipelineBoards(structure.columns);
}

function freeBoard(row: StructureRow, ordinal: number): Board {
  const { item } = row;
  const steps: BoardStep[] = [
    { id: 'request', step: row.request },
    { id: 'execution', step: row.execution },
    { id: 'result', step: row.result },
  ];
  const agents = agentCards(item);
  const columns: BoardColumn[] = [
    { step: steps[0]!, cards: [{ kind: 'request', id: `request:${item.runId}`, item }], count: 1 },
    {
      step: steps[1]!,
      cards: [{ kind: 'execution', id: `execution:${item.runId}`, item }, ...agents],
      count: 1 + agents.length,
    },
    { step: steps[2]!, cards: [{ kind: 'result', id: `result:${item.runId}`, item }], count: item.finished ? 1 : 0 },
  ];
  return withHeadline({ id: item.runId, kind: 'request', ordinal, items: [item], status: item.status, steps, columns });
}

/**
 * A styled pane's pipelines: runs of consecutive requests whose phase never goes back. The
 * newest pipeline's current phase is the pane's, unless the pane's phase comes before the
 * pipeline's last request (the next request starts anew); an older pipeline's is its last
 * request's, done once its requests are.
 */
function pipelineBoards(columns: readonly StructureColumn[]): Board[] {
  const phaseOf = new Map<StructureItem, number>();
  const items: StructureItem[] = [];
  columns.forEach((column, phase) => {
    for (const item of column.items) {
      phaseOf.set(item, phase);
      items.push(item);
    }
  });
  items.sort((a, b) => a.ordinal - b.ordinal);
  const groups: StructureItem[][] = [];
  let previous: number | undefined;
  for (const item of items) {
    const phase = phaseOf.get(item) ?? 0;
    const open = groups[groups.length - 1];
    if (open && previous !== undefined && phase >= previous) open.push(item);
    else groups.push([item]);
    previous = phase;
  }
  const paneIndex = columns.findIndex((column) => column.step === 'current');
  const pane = paneIndex >= 0 ? paneIndex : undefined;
  return groups.map((group, index) => {
    const lastItem = group[group.length - 1]!;
    const last = phaseOf.get(lastItem) ?? 0;
    const newest = index === groups.length - 1;
    const current = newest && pane !== undefined && pane >= last ? pane : last;
    const over = !newest && group.every((item) => item.finished);
    const boardColumns: BoardColumn[] = columns.map((column, phase) => {
      const step: StructureStep = phase < current ? 'done' : phase > current ? 'pending' : over ? 'done' : 'current';
      const held = group.filter((item) => phaseOf.get(item) === phase);
      return {
        step: { id: column.id, title: column.title, step },
        cards: held.flatMap((item): BoardCard[] => [
          { kind: 'summary', id: `summary:${item.runId}`, item },
          ...agentCards(item),
        ]),
        count: held.length,
      };
    });
    const status = group.some((item) => item.status === 'running')
      ? 'running'
      : group.some((item) => item.status === 'waiting')
        ? 'waiting'
        : lastItem.status;
    return withHeadline({
      id: group[0]!.runId,
      kind: 'pipeline',
      ordinal: index + 1,
      items: group,
      status,
      steps: boardColumns.map((column) => column.step),
      columns: boardColumns,
    });
  });
}

function withHeadline(board: Board): Board {
  const line = board.items[0]?.request?.split('\n').map((part) => part.trim()).find((part) => part.length > 0);
  return line ? { ...board, headline: line } : board;
}

/**
 * A request's sub-agent cards, each tree depth-first, each naming the block it runs under:
 * its title, or its kind when it has none, as its own card names it.
 */
function agentCards(item: StructureItem): BoardCard[] {
  const cards: BoardCard[] = [];
  for (const cluster of item.clusters) {
    const titles: string[] = [];
    for (const node of cluster.nodes) {
      titles.length = Math.min(node.depth, titles.length);
      const parent = titles[titles.length - 1];
      cards.push({
        kind: 'agent',
        id: `agent:${item.runId}:${node.id}`,
        node,
        runId: item.runId,
        ...(parent !== undefined ? { parent } : {}),
      });
      titles.push(node.title || blockKindLabel(node.kind));
    }
  }
  return cards;
}

/** The current step's place, else the last step that is over, else the first. */
export function boardCurrentStep(board: Board): number {
  const current = board.steps.findIndex((step) => step.step === 'current');
  if (current >= 0) return current;
  for (let index = board.steps.length - 1; index >= 0; index -= 1) {
    if (board.steps[index]?.step !== 'pending') return index;
  }
  return 0;
}

/**
 * A column's colour (the Mac's `MightyStructure.Tone.column`): its step's, the current one
 * waiting while the column's newest request waits.
 */
export function columnTone(column: BoardColumn): Tone | undefined {
  let newest: StructureItem | undefined;
  for (const card of column.cards) if (card.kind !== 'agent') newest = card.item;
  return stepTone(column.step.step, newest?.status === 'waiting');
}

/**
 * A card's colour (`MightyStructure.Tone.card`): a free board's 요청 and 결과 stand for their
 * step and take their column's; the 실행 card, a pipeline's request and every sub-agent, task
 * and 작업 알림 card take their own status's.
 */
export function cardTone(card: BoardCard, column: BoardColumn): Tone | undefined {
  switch (card.kind) {
    case 'request':
    case 'result':
      return columnTone(column);
    case 'agent':
      return pipelineTone(card.node.status);
    default:
      return pipelineTone(card.item.status);
  }
}

/** A free step's localized name, or the phase's title. */
export function boardStepName(step: BoardStep): string {
  if (step.title !== undefined) return step.title;
  if (step.id === 'request') return t('graph.structure.column.request');
  if (step.id === 'execution') return t('graph.structure.column.execution');
  return t('graph.structure.column.result');
}

/**
 * "요청 3 / 7" or "파이프라인 2 / 3". A request board counts the `omitted` older requests the
 * Mac left out before it, so its place is its request's number, as the Mac and the card
 * heading say it; `omitted` counts requests, so a pipeline's place counts the boards held.
 */
export function boardPosition(board: Board, index: number, count: number, omitted = 0): string {
  if (board.kind === 'request') return t('graph.structure.board.request', { n: omitted + index + 1, count: omitted + count });
  return t('graph.structure.board.pipeline', { n: index + 1, count });
}

/** The board's words: its first request's first line, else that request's style name, else its place. */
export function boardTitle(board: Board, count: number, omitted = 0): string {
  return board.headline ?? board.items[0]?.title ?? boardPosition(board, board.ordinal - 1, count, omitted);
}

const STEP_MARKS: Record<StructureStep, string> = {
  done: '✓',
  current: '●',
  pending: '○',
  error: '✕',
  stopped: '■',
};

/** "요청 ✓ → 실행 ● → 결과 ○": each step and where it stands. */
export function boardStepsSummary(board: Board): string {
  return board.steps.map((step) => `${boardStepName(step)} ${STEP_MARKS[step.step]}`).join(' → ');
}

/** A column header read out: "실행: 진행 중, 카드 3개". */
export function boardColumnAccessibility(column: BoardColumn): string {
  return t('graph.structure.board.columnAccessibility', {
    name: boardStepName(column.step),
    state: stepStateWord(column.step.step),
    count: column.count,
  });
}

// ------------------------------------------------------------------ which board shows

/**
 * Which board the view shows, as the Mac's `MightyStructure.Navigation`. While the user is
 * on the newest board the view moves on with it. A board the user picks that is not the
 * newest is kept by its id, and by the requests it held, so new requests never move it; a
 * newer board that arrives meanwhile is unseen until the user goes to the newest again.
 */
export interface BoardNavigation {
  /** The board the user picked; absent while the newest is shown. */
  chosen?: string;
  chosenRuns: readonly string[];
  /** The newest board when the user left it. */
  seenNewest?: string;
}

export const NEWEST_BOARD: BoardNavigation = { chosenRuns: [] };

/**
 * The place of the board shown, undefined when there is none. A picked board that is gone
 * is found by a request it held; with none of them left the newest board is shown, as if
 * nothing were picked, so no newer board reads as unseen.
 */
export function boardIndex(navigation: BoardNavigation, boards: readonly Board[]): number | undefined {
  if (boards.length === 0) return undefined;
  if (navigation.chosen === undefined) return boards.length - 1;
  const index = boards.findIndex((board) => board.id === navigation.chosen);
  if (index >= 0) return index;
  const runs = new Set(navigation.chosenRuns);
  const holding = boards.findIndex((board) => board.items.some((item) => runs.has(item.runId)));
  return holding >= 0 ? holding : boards.length - 1;
}

export function onNewestBoard(navigation: BoardNavigation, boards: readonly Board[]): boolean {
  const index = boardIndex(navigation, boards);
  return index === undefined || index === boards.length - 1;
}

/** A board newer than the one shown arrived since the user left the newest. */
export function hasUnseenBoard(navigation: BoardNavigation, boards: readonly Board[]): boolean {
  return !onNewestBoard(navigation, boards) && boards[boards.length - 1]?.id !== navigation.seenNewest;
}

/** Shows board `id`; the newest board is shown as "the newest", so the view moves on again. */
export function selectBoard(navigation: BoardNavigation, id: string, boards: readonly Board[]): BoardNavigation {
  const target = boards.find((board) => board.id === id);
  if (!target) return navigation;
  const newest = boards[boards.length - 1];
  if (target.id === newest?.id) return NEWEST_BOARD;
  return {
    chosen: target.id,
    chosenRuns: target.items.map((item) => item.runId),
    seenNewest: onNewestBoard(navigation, boards) ? newest?.id : navigation.seenNewest,
  };
}

/** Shows the board `offset` places from the shown one, stopping at the ends. */
export function stepBoard(navigation: BoardNavigation, offset: number, boards: readonly Board[]): BoardNavigation {
  const index = boardIndex(navigation, boards);
  if (index === undefined) return navigation;
  const target = boards[Math.min(Math.max(0, index + offset), boards.length - 1)];
  return target ? selectBoard(navigation, target.id, boards) : navigation;
}

// ------------------------------------------------------------------ which column shows

/**
 * The column page shown: the step the user moved to (held by its id), else the board's
 * current step, which the pager follows while the user has not moved.
 */
export function visibleColumn(board: Board, chosen: string | undefined): number {
  const index = chosen === undefined ? -1 : board.steps.findIndex((step) => step.id === chosen);
  return index >= 0 ? index : boardCurrentStep(board);
}

/** The choice a swipe or a tap leaves: none on the current step, so the pager follows it again. */
export function chosenColumnAfterMove(board: Board, landed: number): string | undefined {
  if (landed === boardCurrentStep(board)) return undefined;
  return board.steps[landed]?.id;
}

/** The choice once the board moves on: dropped when the current step reaches it, so following resumes. */
export function chosenColumnAfterUpdate(board: Board, chosen: string | undefined): string | undefined {
  return chosen !== undefined && board.steps[boardCurrentStep(board)]?.id === chosen ? undefined : chosen;
}
