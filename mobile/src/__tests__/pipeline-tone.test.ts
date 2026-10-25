import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { MobileStructure, StructureColumn, StructureItem, StructureNode, StructureRow, StructureStep } from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import { cardTone, columnTone, structureBoards, type Board } from '@/lib/pipeline-board';
import { pipelineTone, stepTone, type Tone } from '@/lib/status-tone';
import { darkPalette, lightPalette, pipeline, tintOver, toneColors, toneMark } from '@/theme';

/**
 * The 파이프라인 view's state colours, held to the Mac's `MightyStructureToneTests`: the same
 * tone for a status, a step, a column (a current one takes its newest request's) and a card, and the same tints, bar and tone colours as
 * the contract (`native/contracts/fixtures/design-tokens.json` `pipeline` and `derived`).
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/design-tokens.json');
const contract = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  pipeline: Record<string, number>;
  derived: Record<'light' | 'dark', { tones: Record<Tone, { soft: string; mark: string }> }>;
};

beforeAll(() => resetLanguage('en'));
afterAll(() => resetLanguage());

function item(ordinal: number, overrides: Partial<StructureItem> = {}): StructureItem {
  return { runId: `r${ordinal}`, ordinal, status: 'completed', finished: true, activity: [], tally: { agents: 0, tools: 0 }, clusters: [], ...overrides };
}

function node(id: string, status: string, depth = 0): StructureNode {
  return { id, depth, title: id, status, kind: 'agent' };
}

function row(entry: StructureItem, request: StructureStep, execution: StructureStep, result: StructureStep): StructureRow {
  return { item: entry, request, execution, result };
}

function free(rows: StructureRow[]): MobileStructure {
  return { v: 1, shape: 'free', rows };
}

function columnTones(board: Board): (Tone | undefined)[] {
  return board.columns.map((column) => columnTone(column));
}

/** Six phases, the pane at `current` (the third, 시드, unless given), holding `held` by phase id. */
function phases(held: Record<string, StructureItem[]>, current = 2): MobileStructure {
  const ids = ['goal', 'interview', 'seed', 'run', 'evaluate', 'evolve'];
  const columns: StructureColumn[] = ids.map((id, index) => ({
    id,
    title: id,
    step: index < current ? 'done' : index === current ? 'current' : 'pending',
    items: held[id] ?? [],
  }));
  return { v: 1, shape: 'phases', columns };
}

describe('pipeline tones', () => {
  it('gives a status the block bucket, done its own and pending none', () => {
    const words = ['running', 'starting', 'queued', 'waiting', 'completed', 'done', 'error', 'failed', 'stopped', 'cancelled', 'interrupted'];
    expect(words.map((word) => pipelineTone(word))).toEqual(['run', 'run', 'run', 'wait', 'done', 'done', 'err', 'err', 'stop', 'stop', 'stop']);
    expect(pipelineTone('pending')).toBeUndefined();
  });

  it('gives a step its tone, the current one waiting while its request waits', () => {
    const steps: StructureStep[] = ['done', 'current', 'pending', 'error', 'stopped'];
    expect(steps.map((step) => stepTone(step))).toEqual(['done', 'run', undefined, 'err', 'stop']);
    expect(stepTone('current', true)).toBe('wait');
    expect(stepTone('pending', true)).toBeUndefined();
    expect(stepTone('done', true)).toBe('done');
  });

  it('tints a free board: 요청 done, 실행 by its request, 결과 none until it ends', () => {
    const waiting = item(1, { status: 'waiting', finished: false, clusters: [{ nodes: [node('a', 'running')] }] });
    const board = structureBoards(free([row(waiting, 'done', 'current', 'pending')]))[0]!;
    expect(columnTones(board)).toEqual(['done', 'wait', undefined]);
    const execution = board.columns[1]!;
    expect(execution.cards.map((card) => cardTone(card, execution))).toEqual(['wait', 'run']);
    // The 요청 and 결과 cards stand for their step.
    expect(cardTone(board.columns[0]!.cards[0]!, board.columns[0]!)).toBe('done');
    expect(cardTone(board.columns[2]!.cards[0]!, board.columns[2]!)).toBeUndefined();
    const ended = structureBoards(
      free([
        row(item(1), 'done', 'done', 'done'),
        row(item(2, { status: 'error' }), 'done', 'error', 'error'),
        row(item(3, { status: 'stopped' }), 'done', 'stopped', 'stopped'),
        row(item(4, { status: 'running', finished: false }), 'done', 'current', 'pending'),
      ]),
    );
    expect(ended.map(columnTones)).toEqual([
      ['done', 'done', 'done'],
      ['done', 'err', 'err'],
      ['done', 'stop', 'stop'],
      ['done', 'run', undefined],
    ]);
    expect(ended.map((each) => cardTone(each.columns[2]!.cards[0]!, each.columns[2]!))).toEqual(['done', 'err', 'stop', undefined]);
  });

  it('tints a pipeline by its phases, each card by its own status', () => {
    const waiting = structureBoards(
      phases({
        interview: [item(1, { clusters: [{ nodes: [node('x', 'completed'), node('y', 'error', 1)] }] })],
        seed: [item(2, { status: 'waiting', finished: false })],
      }),
    )[0]!;
    expect(columnTones(waiting)).toEqual(['done', 'done', 'wait', undefined, undefined, undefined]);
    const interview = waiting.columns[1]!;
    expect(interview.cards.map((card) => cardTone(card, interview))).toEqual(['done', 'done', 'err']);
    const running = structureBoards(phases({ interview: [item(1)], seed: [item(2, { status: 'running', finished: false })] }))[0]!;
    expect(columnTones(running)).toEqual(['done', 'done', 'run', undefined, undefined, undefined]);
  });

  it("gives a current column its newest request's status, and runs while it holds none", () => {
    const seedColumn = (status: string) => {
      const board = structureBoards(phases({ interview: [item(1)], seed: [item(2, { status })] }))[0]!;
      expect(board.columns[2]!.step.step).toBe('current');
      return columnTone(board.columns[2]!);
    };
    // The pipeline stands at 시드 with its request over: the step reads as that request ended.
    expect(['completed', 'stopped', 'cancelled', 'failed'].map(seedColumn)).toEqual(['done', 'stop', 'stop', 'err']);
    // The pane stands at 실행 for the next request, which no request reached yet: the current step runs.
    const moved = structureBoards(phases({ interview: [item(1)], seed: [item(2)] }, 3))[0]!;
    expect(moved.columns[3]!.step.step).toBe('current');
    expect(moved.columns[3]!.cards).toEqual([]);
    expect(columnTone(moved.columns[3]!)).toBe('run');
  });
});

describe('pipeline colours', () => {
  it('keeps the contract tints and bar (pipeline)', () => {
    expect(Object.keys(contract.pipeline).sort()).toEqual(Object.keys(pipeline).sort());
    for (const [name, value] of Object.entries(contract.pipeline)) expect(pipeline[name as keyof typeof pipeline]).toBe(value);
    // The card is lighter than its column, so it stands out from a column of its own tone.
    expect(pipeline).toEqual({ columnTintOpacity: 0.6, cardTintOpacity: 0.35, cardBarWidth: 3 });
  });

  it.each([
    ['light', lightPalette],
    ['dark', darkPalette],
  ] as const)('draws every tone in the contract soft and mark (%s)', (mode, palette) => {
    for (const tone of ['run', 'wait', 'done', 'err', 'stop', 'idle'] as const) {
      expect(toneColors(palette, tone).soft).toBe(contract.derived[mode].tones[tone].soft);
      expect(toneMark(palette, tone)).toBe(contract.derived[mode].tones[tone].mark);
    }
  });

  it('lays a tint on a surface as one solid colour', () => {
    expect(tintOver('#000000', '#FFFFFF', 0.5)).toBe('#808080');
    expect(tintOver('#ECEEF3', '#E6EDFF', 0)).toBe('#ECEEF3');
    expect(tintOver('#ECEEF3', '#E6EDFF', 1)).toBe('#E6EDFF');
  });
});
