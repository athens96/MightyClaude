import { readFileSync } from 'fs';
import { resolve } from 'path';
import type { MobileStructure, StructureColumn, StructureItem, StructureNode, StructureRow, StructureStep } from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import {
  NEWEST_BOARD,
  boardColumnAccessibility,
  boardCurrentStep,
  boardIndex,
  boardPosition,
  boardStepsSummary,
  boardTitle,
  chosenColumnAfterMove,
  chosenColumnAfterUpdate,
  hasUnseenBoard,
  onNewestBoard,
  selectBoard,
  stepBoard,
  structureBoards,
  visibleColumn,
  type BoardCard,
} from '@/lib/pipeline-board';
import { blockKindLabel } from '@/lib/mighty';
import { normalizeStructure } from '@/lib/structure';

/**
 * The 파이프라인 boards, held to the Mac's `MightyStructureBoardTests`: the same grouping,
 * steps, columns, counts and navigation, read off the structure the Mac sends.
 */

const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/mobile-structure.json');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as { styled: unknown; free: unknown };

beforeAll(() => resetLanguage('en'));
afterAll(() => resetLanguage());

function item(ordinal: number, overrides: Partial<StructureItem> = {}): StructureItem {
  return {
    runId: `r${ordinal}`,
    ordinal,
    status: 'completed',
    finished: true,
    activity: [],
    tally: { agents: 0, tools: 0 },
    clusters: [],
    ...overrides,
  };
}

function node(id: string, depth: number, status = 'completed'): StructureNode {
  return { id, depth, title: id.toUpperCase(), status, kind: 'agent' };
}

function row(entry: StructureItem, request: StructureStep, execution: StructureStep, result: StructureStep): StructureRow {
  return { item: entry, request, execution, result };
}

function free(rows: StructureRow[]): MobileStructure {
  return { v: 1, shape: 'free', rows };
}

/** Ouroboros' six phases, the pane standing at `current`, holding `items` by phase. */
function styled(current: string | undefined, items: Record<string, StructureItem[]>): MobileStructure {
  const phases = ['goal', 'interview', 'seed', 'run', 'evaluate', 'evolve'];
  const at = current === undefined ? -1 : phases.indexOf(current);
  const columns: StructureColumn[] = phases.map((id, index) => ({
    id,
    title: id.toUpperCase(),
    step: index < at ? 'done' : index === at ? 'current' : 'pending',
    items: items[id] ?? [],
  }));
  return { v: 1, shape: 'phases', columns };
}

const cardIds = (cards: readonly BoardCard[]) => cards.map((card) => card.id);

describe('free boards', () => {
  it('has one board per request with 요청 → 실행 → 결과', () => {
    const running = item(2, {
      status: 'running',
      finished: false,
      clusters: [{ nodes: [node('a', 0, 'running'), node('b', 1, 'running')] }, { nodes: [node('c', 0)] }],
    });
    const boards = structureBoards(
      free([
        row(item(1, { request: '\n  first line\nsecond line', answer: 'done' }), 'done', 'done', 'done'),
        row(running, 'done', 'current', 'pending'),
        row(item(3, { status: 'error' }), 'done', 'error', 'error'),
      ]),
    );
    expect(boards.map((board) => board.id)).toEqual(['r1', 'r2', 'r3']);
    expect(boards.map((board) => board.ordinal)).toEqual([1, 2, 3]);
    expect(boards.every((board) => board.kind === 'request' && board.items.length === 1)).toBe(true);
    expect(boards[0]?.headline).toBe('first line');
    expect(boards[1]?.headline).toBeUndefined();
    expect(boards.map((board) => board.status)).toEqual(['completed', 'running', 'error']);
    expect(boards[0]?.steps.map((step) => step.id)).toEqual(['request', 'execution', 'result']);
    expect(boards[0]?.steps.every((step) => step.title === undefined)).toBe(true);
    expect(boards.map(boardCurrentStep)).toEqual([2, 1, 2]);

    const board = boards[1]!;
    expect(board.columns.map((column) => column.step)).toEqual(board.steps);
    expect(cardIds(board.columns[0]!.cards)).toEqual(['request:r2']);
    expect(cardIds(board.columns[1]!.cards)).toEqual(['execution:r2', 'agent:r2:a', 'agent:r2:b', 'agent:r2:c']);
    expect(cardIds(board.columns[2]!.cards)).toEqual(['result:r2']);
    expect(board.columns.map((column) => column.count)).toEqual([1, 4, 0]);
    expect(boards[0]?.columns.map((column) => column.count)).toEqual([1, 1, 1]);
    // A nested sub-agent names the block it runs under; a root names none.
    const parents = board.columns[1]!.cards.flatMap((card) => (card.kind === 'agent' ? [card.parent ?? null] : []));
    expect(parents).toEqual([null, 'A', null]);
    // A parent with no title is named by its kind, as its own card is.
    const untitled = structureBoards(
      free([row(item(4, { clusters: [{ nodes: [{ ...node('p', 0), title: '' }, node('q', 1)] }] }), 'done', 'current', 'pending')]),
    )[0]!;
    const nested = untitled.columns[1]!.cards[2];
    expect(nested?.kind === 'agent' ? nested.parent : undefined).toBe(blockKindLabel('agent'));
    // The request cards keep the structure's own objects, so memoised cards skip a redraw.
    const first = board.columns[1]!.cards[0];
    expect(first?.kind === 'execution' && first.item).toBe(running);
  });
});

describe('pipelines', () => {
  it('splits a styled pane where the phase goes back', () => {
    const boards = structureBoards(
      styled('interview', {
        goal: [item(1, { request: 'just start' })],
        interview: [item(2), item(3), item(5), item(6, { status: 'running', finished: false })],
        seed: [item(4)],
      }),
    );
    expect(boards.map((board) => board.id)).toEqual(['r1', 'r5']);
    expect(boards.map((board) => board.items.map((entry) => entry.runId))).toEqual([
      ['r1', 'r2', 'r3', 'r4'],
      ['r5', 'r6'],
    ]);
    expect(boards.every((board) => board.kind === 'pipeline')).toBe(true);
    expect(boards.map((board) => board.ordinal)).toEqual([1, 2]);
    expect(boards[0]?.headline).toBe('just start');
    expect(boards.map((board) => board.status)).toEqual(['completed', 'running']);
    expect(boards[0]?.steps.map((step) => step.title)).toEqual(['GOAL', 'INTERVIEW', 'SEED', 'RUN', 'EVALUATE', 'EVOLVE']);
    // An older pipeline that is over: every phase it reached is done, the rest pending.
    expect(boards[0]?.steps.map((step) => step.step)).toEqual(['done', 'done', 'done', 'pending', 'pending', 'pending']);
    // The newest pipeline stands where the pane does.
    expect(boards[1]?.steps.map((step) => step.step)).toEqual(['done', 'current', 'pending', 'pending', 'pending', 'pending']);
    expect(boardCurrentStep(boards[1]!)).toBe(1);
    expect(structureBoards(styled('goal', { goal: [item(1)] })).map((board) => board.id)).toEqual(['r1']);
    expect(structureBoards(styled('goal', {}))).toEqual([]);
  });

  it('lets the newest pipeline take the pane phase only when it lies ahead', () => {
    const runs = { interview: [item(1)], seed: [item(2)] };
    const moved = structureBoards(styled('run', runs));
    expect(moved[0]?.steps.map((step) => step.step)).toEqual(['done', 'done', 'done', 'current', 'pending', 'pending']);
    const behind = structureBoards(styled('goal', runs));
    expect(behind.map((board) => board.items.length)).toEqual([2]);
    expect(behind[0]?.steps.map((step) => step.step)).toEqual(['done', 'done', 'current', 'pending', 'pending', 'pending']);
    // An older pipeline whose last request still runs stays current there.
    const busy = structureBoards(
      styled(undefined, { seed: [item(1, { status: 'running', finished: false })], interview: [item(2)] }),
    );
    expect(busy.map((board) => board.id)).toEqual(['r1', 'r2']);
    expect(busy[0]?.steps.map((step) => step.step)).toEqual(['done', 'done', 'current', 'pending', 'pending', 'pending']);
  });

  it('holds each request in its phase column with its sub-agents under it', () => {
    const board = structureBoards(
      styled('seed', {
        interview: [item(1, { clusters: [{ nodes: [node('x', 0), node('y', 1)] }] }), item(2)],
        seed: [item(3, { status: 'running', finished: false })],
      }),
    )[0]!;
    expect(board.columns.map((column) => cardIds(column.cards))).toEqual([
      [],
      ['summary:r1', 'agent:r1:x', 'agent:r1:y', 'summary:r2'],
      ['summary:r3'],
      [],
      [],
      [],
    ]);
    expect(board.columns.map((column) => column.count)).toEqual([0, 2, 1, 0, 0, 0]);
    expect(board.steps.map((step) => step.step)).toEqual(['done', 'done', 'current', 'pending', 'pending', 'pending']);
  });

  it('reads the shared fixture into boards', () => {
    const styledState = normalizeStructure(fixture.styled);
    const freeState = normalizeStructure(fixture.free);
    if (styledState.kind !== 'ok' || freeState.kind !== 'ok') throw new Error('fixture unreadable');
    const pipelines = structureBoards(styledState.structure);
    expect(pipelines.map((board) => board.items.map((entry) => entry.runId))).toEqual([['run-3', 'run-4']]);
    expect(pipelines[0]?.steps.map((step) => step.step)).toEqual(['done', 'done', 'current', 'pending']);
    expect(pipelines[0]?.status).toBe('running');
    const requests = structureBoards(freeState.structure);
    expect(requests.map((board) => board.id)).toEqual(freeState.structure.shape === 'free'
      ? freeState.structure.rows.map((entry) => entry.item.runId)
      : []);
  });
});

describe('words', () => {
  const boards = structureBoards(
    free([row(item(1, { request: 'Fix it' }), 'done', 'done', 'done'), row(item(2, { title: 'Seed' }), 'done', 'current', 'pending')]),
  );

  it('names a board by its place, its first line, else its style name', () => {
    expect(boardPosition(boards[0]!, 0, 2)).toBe('Request 1 / 2');
    expect(boardTitle(boards[0]!, 2)).toBe('Fix it');
    expect(boardTitle(boards[1]!, 2)).toBe('Seed');
    expect(boardTitle({ ...boards[1]!, items: [item(2)] }, 2)).toBe('Request 2 / 2');
    const pipeline = structureBoards(styled('goal', { goal: [item(1)] }))[0]!;
    expect(boardPosition(pipeline, 0, 1)).toBe('Pipeline 1 / 1');
    // Older requests the Mac left out count in a request board's place, as its heading's number does.
    expect(boardPosition(boards[0]!, 0, 2, 5)).toBe('Request 6 / 7');
    expect(boardTitle({ ...boards[1]!, items: [item(2)] }, 2, 5)).toBe('Request 7 / 7');
    // They are requests, not pipelines: a pipeline's place counts the boards held.
    expect(boardPosition(pipeline, 0, 1, 5)).toBe('Pipeline 1 / 1');
  });

  it('sums up the steps and reads a column out', () => {
    expect(boardStepsSummary(boards[1]!)).toBe('Request ✓ → Run ● → Result ○');
    expect(boardColumnAccessibility(boards[1]!.columns[1]!)).toBe('Run: In progress, cards: 1');
  });
});

describe('navigation', () => {
  const boardsOf = (count: number) =>
    structureBoards(free(Array.from({ length: count }, (_, index) => row(item(index + 1), 'done', 'done', 'done'))));

  it('moves on with the newest board until the user picks an older one', () => {
    let nav = NEWEST_BOARD;
    expect(boardIndex(nav, [])).toBeUndefined();
    expect(boardIndex(nav, boardsOf(3))).toBe(2);
    expect(onNewestBoard(nav, boardsOf(3))).toBe(true);
    expect(boardIndex(nav, boardsOf(4))).toBe(3);
    expect(hasUnseenBoard(nav, boardsOf(4))).toBe(false);
    // Back one: the older board is kept by id as new ones arrive, and they are unseen.
    nav = stepBoard(nav, -1, boardsOf(4));
    expect(nav.chosen).toBe('r3');
    expect(hasUnseenBoard(nav, boardsOf(4))).toBe(false);
    expect(boardIndex(nav, boardsOf(5))).toBe(2);
    expect(hasUnseenBoard(nav, boardsOf(5))).toBe(true);
    // Moving between older boards keeps the badge until the newest is shown.
    nav = selectBoard(nav, 'r1', boardsOf(5));
    expect(boardIndex(nav, boardsOf(5))).toBe(0);
    expect(hasUnseenBoard(nav, boardsOf(5))).toBe(true);
    nav = stepBoard(nav, -1, boardsOf(5));
    expect(boardIndex(nav, boardsOf(5))).toBe(0);
    nav = selectBoard(nav, 'missing', boardsOf(5));
    expect(boardIndex(nav, boardsOf(5))).toBe(0);
    nav = selectBoard(nav, 'r5', boardsOf(5));
    expect(nav).toBe(NEWEST_BOARD);
    expect(boardIndex(nav, boardsOf(6))).toBe(5);
  });

  it('finds a picked board again by a request it held, else shows the newest', () => {
    // Three pipelines, split where the phase goes back: r1 (seed); r2 (goal) → r3 (interview); r4 (goal).
    const boards = structureBoards(
      styled('goal', { seed: [item(1)], goal: [item(2), item(4)], interview: [item(3)] }),
    );
    expect(boards.map((board) => board.id)).toEqual(['r1', 'r2', 'r4']);
    const nav = selectBoard(NEWEST_BOARD, 'r2', boards);
    // The middle pipeline loses its first request: it is named by its next one and found by it.
    const trimmed = structureBoards(styled('goal', { seed: [item(1)], goal: [item(4)], interview: [item(3)] }));
    expect(trimmed.map((board) => board.id)).toEqual(['r1', 'r3', 'r4']);
    expect(boardIndex(nav, trimmed)).toBe(1);
    // No newer board arrived, so none reads as unseen.
    expect(hasUnseenBoard(nav, trimmed)).toBe(false);
    // Every request it held is gone: the newest board shows, with no badge.
    const gone = structureBoards(styled('goal', { seed: [item(1)], goal: [item(4)] }));
    expect(boardIndex(nav, gone)).toBe(gone.length - 1);
    expect(hasUnseenBoard(nav, gone)).toBe(false);
  });

  it('opens a board on its current step and keeps a column the user moved to', () => {
    const board = boardsOf(1)[0]!;
    const running = structureBoards(free([row(item(1), 'done', 'current', 'pending')]))[0]!;
    expect(visibleColumn(board, undefined)).toBe(2);
    expect(visibleColumn(running, undefined)).toBe(1);
    expect(visibleColumn(running, 'request')).toBe(0);
    expect(visibleColumn(running, 'gone')).toBe(1);
    expect(chosenColumnAfterMove(running, 0)).toBe('request');
    expect(chosenColumnAfterMove(running, 1)).toBeUndefined();
    // The current step reaching the picked column drops the pick, so the pager follows again.
    expect(chosenColumnAfterUpdate(running, 'result')).toBe('result');
    const resulting = structureBoards(free([row(item(1), 'done', 'done', 'current')]))[0]!;
    expect(chosenColumnAfterUpdate(resulting, 'result')).toBeUndefined();
    expect(chosenColumnAfterUpdate(resulting, undefined)).toBeUndefined();
  });
});
