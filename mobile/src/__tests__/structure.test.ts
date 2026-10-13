import { readFileSync } from 'fs';
import { resolve } from 'path';
import type { StructureColumn, StructureItem, StructureNode, StructureRow } from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import {
  FOLD_LINES,
  FOLD_LINE_CHARS,
  MAX_STRUCTURE_DEPTH,
  MAX_STRUCTURE_ITEMS,
  MAX_STRUCTURE_NODES,
  MAX_STRUCTURE_PROMPT,
  chosenAfterMove,
  currentPageIndex,
  firstTextLine,
  foldText,
  foldedRequestLine,
  foldedResultLine,
  isCardFolded,
  itemHeading,
  nodeFolds,
  nodeMeta,
  normalizeStructure,
  openFence,
  pageAtOffset,
  pageLabel,
  parseStep,
  phaseRail,
  receiveStructure,
  requestRail,
  resultHeading,
  stabilizeStructure,
  stepLook,
  structureItems,
  structureNotice,
  structureQuery,
  toggleCardFold,
  visiblePage,
  type HeldStructure,
} from '@/lib/structure';

// The same file the Mac's MobileRemoteStructureTests decodes: what it writes, this reads.
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/mobile-structure.json');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as { styled: unknown; free: unknown };

beforeAll(() => resetLanguage('en'));
afterAll(() => resetLanguage());

function column(id: string, step: string, ordinals: number[] = []): StructureColumn {
  return {
    id,
    title: id,
    step: parseStep(step),
    items: ordinals.map((ordinal) => ({
      runId: `r${ordinal}`,
      ordinal,
      status: 'completed',
      finished: true,
      activity: [],
      tally: { agents: 0, tools: 0 },
      clusters: [],
    })),
  };
}

describe('normalizeStructure', () => {
  it('reads a styled payload as the Mac wrote it', () => {
    const state = normalizeStructure(fixture.styled);
    if (state.kind !== 'ok' || state.structure.shape !== 'phases') throw new Error('expected phases');
    const { columns } = state.structure;
    expect(columns.map((each) => [each.id, each.step])).toEqual([
      ['goal', 'done'],
      ['interview', 'done'],
      ['seed', 'current'],
      ['run', 'pending'],
    ]);
    expect(state.structure.omitted).toBe(2);
    const seed = columns[2]?.items[0];
    expect(seed?.title).toBe('Seed');
    expect(seed?.activity.map((line) => line.text)).toEqual(['interview.md', 'seed.yaml']);
    expect(seed?.clusters[0]?.nodes.map((node) => [node.id, node.depth])).toEqual([
      ['agent-a', 0],
      ['agent-b', 1],
    ]);
    expect(seed?.clusters[0]?.nodes[0]?.activity?.text).toBe('criteria');
    expect(seed?.omittedNodes).toBe(4);
    expect(columns[1]?.items[0]?.answer).toContain('## Questions');
  });

  it('reads a free payload as the Mac wrote it', () => {
    const state = normalizeStructure(fixture.free);
    if (state.kind !== 'ok' || state.structure.shape !== 'free') throw new Error('expected free');
    const { rows } = state.structure;
    expect(rows.map((row) => [row.item.runId, row.request, row.execution, row.result])).toEqual([
      ['run-1', 'done', 'done', 'done'],
      ['run-2', 'done', 'current', 'pending'],
    ]);
    expect(rows[1]?.item.clusters[0]?.nodes[0]?.kind).toBe('task');
    expect(structureItems(state.structure).map((item) => item.ordinal)).toEqual([1, 2]);
  });

  it('says why there is nothing to draw', () => {
    expect(structureNotice({ kind: 'unsupported' }, true)).toBe('needsMacUpdate');
    expect(structureNotice({ kind: 'unsupported' }, false)).toBe('empty');
    expect(structureNotice({ kind: 'none' }, true)).toBe('empty');
    expect(structureNotice({ kind: 'unreadable' }, true)).toBe('needsAppUpdate');
    expect(structureNotice({ kind: 'loading' }, true)).toBe('loading');
    expect(normalizeStructure(undefined)).toEqual({ kind: 'none' });
    expect(normalizeStructure(null)).toEqual({ kind: 'none' });
  });

  it('never crashes on malformed input and says when it cannot read it', () => {
    for (const raw of [
      42,
      'x',
      [],
      {},
      { v: 2, shape: 'free', rows: [] },
      { v: 1, shape: 'grid' },
      { v: 1, shape: 'phases', columns: 'no' },
      { v: 1, shape: 'free', rows: 'no' },
      { v: 1, shape: 'free' },
    ]) {
      expect(normalizeStructure(raw)).toEqual({ kind: 'unreadable' });
    }

    const state = normalizeStructure({
      v: 1,
      shape: 'free',
      omitted: -3,
      rows: [
        null,
        { item: { runId: '' } },
        { item: 'nope' },
        {
          item: {
            runId: 'r1',
            ordinal: 'one',
            status: 7,
            finished: 'yes',
            activity: [{ text: '' }, { kind: 'read', text: 'a‮b' }, 5],
            tally: { agents: -1, tools: Infinity },
            clusters: [{ nodes: [{ id: 'n', depth: 99, title: { x: 1 } }, { depth: 1 }] }, 'x'],
            constructor: 'ignored',
          },
          request: 'sideways',
          execution: '__proto__',
          result: 'done',
        },
      ],
    });
    if (state.kind !== 'ok' || state.structure.shape !== 'free') throw new Error('expected free');
    expect(state.structure.omitted).toBeUndefined();
    const [row] = state.structure.rows;
    expect(state.structure.rows).toHaveLength(1);
    expect(row?.request).toBe('pending');
    expect(row?.execution).toBe('pending');
    expect(row?.item).toMatchObject({ ordinal: 1, status: '', finished: false, tally: { agents: 0, tools: 0 } });
    expect(row?.item.activity).toEqual([{ kind: 'read', state: '', text: 'ab' }]);
    expect(row?.item.clusters).toEqual([{ nodes: [{ id: 'n', depth: MAX_STRUCTURE_DEPTH, title: '', status: '', kind: '' }] }]);
  });

  it('clamps sizes', () => {
    const nodes = Array.from({ length: 70 }, (_, index) => ({ id: `n${index}`, depth: 0, prompt: 'p'.repeat(5_000) }));
    const rows = Array.from({ length: 25 }, (_, index) => ({
      item: { runId: `r${index + 1}`, ordinal: index + 1, clusters: [{ nodes }] },
    }));
    const state = normalizeStructure({ v: 1, shape: 'free', rows, omitted: 3 });
    if (state.kind !== 'ok' || state.structure.shape !== 'free') throw new Error('expected free');
    expect(state.structure.rows).toHaveLength(MAX_STRUCTURE_ITEMS);
    expect(state.structure.rows[0]?.item.runId).toBe('r6');
    expect(state.structure.omitted).toBe(8);
    const item = state.structure.rows[0]?.item;
    expect(item?.clusters.flatMap((cluster) => cluster.nodes)).toHaveLength(MAX_STRUCTURE_NODES);
    expect(item?.omittedNodes).toBe(10);
    expect(item?.clusters[0]?.nodes[0]?.prompt).toHaveLength(MAX_STRUCTURE_PROMPT);

    const columns = [
      { id: 'a', step: 'done', items: rows.slice(0, 15).map((row) => row.item) },
      { id: 'b', step: 'current', items: rows.slice(15).map((row) => row.item) },
    ];
    const phases = normalizeStructure({ v: 1, shape: 'phases', columns });
    if (phases.kind !== 'ok' || phases.structure.shape !== 'phases') throw new Error('expected phases');
    expect(structureItems(phases.structure)).toHaveLength(MAX_STRUCTURE_ITEMS);
    expect(phases.structure.columns[0]?.items[0]?.ordinal).toBe(6);
    expect(phases.structure.omitted).toBe(5);
    // A column with no title falls back to its id.
    expect(phases.structure.columns[0]?.title).toBe('a');

    // Numbers that tie never let more than the cap through.
    const same = Array.from({ length: 30 }, (_, index) => ({ runId: `t${index}`, ordinal: 7 }));
    const tied = normalizeStructure({
      v: 1,
      shape: 'phases',
      columns: [
        { id: 'a', step: 'done', items: same.slice(0, 15) },
        { id: 'b', step: 'current', items: same.slice(15) },
      ],
    });
    if (tied.kind !== 'ok') throw new Error('expected a structure');
    expect(structureItems(tied.structure)).toHaveLength(MAX_STRUCTURE_ITEMS);
    expect(tied.structure.omitted).toBe(10);
  });
});

describe('what the phone holds', () => {
  const tagged = (structureTag: unknown, structure?: unknown) => ({ style: 'cli', runs: [], structureTag, structure });

  it('asks with the tag it holds', () => {
    expect(structureQuery(undefined)).toBe(0);
    const held = receiveStructure(undefined, tagged(41, fixture.free));
    expect(held?.tag).toBe(41);
    expect(held?.state.kind).toBe('ok');
    expect(structureQuery(held)).toBe(41);
  });

  it('keeps what it holds while the tag matches, or when the Mac shed the structure', () => {
    const held = receiveStructure(undefined, tagged(41, fixture.free)) as HeldStructure;
    expect(receiveStructure(held, tagged(41))).toBe(held);
    // A newer tag without the value: the Mac had no room this time; keep showing the old one.
    expect(receiveStructure(held, tagged(42))).toBe(held);
    // A detail that was not asked for the structure changes nothing.
    expect(receiveStructure(held, { style: 'cli', runs: [] })).toBe(held);
    expect(receiveStructure(held, undefined)).toBe(held);
    expect(receiveStructure(held, tagged('41'))).toBe(held);
  });

  it('reads tag 0 as nothing to draw and a bad value as unreadable', () => {
    expect(receiveStructure(undefined, tagged(0))).toEqual({ tag: 0, state: { kind: 'none' } });
    expect(receiveStructure(undefined, tagged(9, { v: 7 }))).toEqual({ tag: 9, state: { kind: 'unreadable' } });
  });

  it('keeps every unchanged request the same object', () => {
    const first = receiveStructure(undefined, tagged(1, fixture.styled)) as HeldStructure;
    const changed = JSON.parse(JSON.stringify(fixture.styled)) as {
      columns: { items: { runId: string; message?: string }[] }[];
    };
    const seed = changed.columns[2]?.items[0];
    if (seed) seed.message = 'Now writing the tests.';
    const second = receiveStructure(first, tagged(2, changed)) as HeldStructure;
    if (first.state.kind !== 'ok' || second.state.kind !== 'ok') throw new Error('expected structures');
    if (first.state.structure.shape !== 'phases' || second.state.structure.shape !== 'phases') throw new Error('phases');
    const [before, after] = [first.state.structure.columns, second.state.structure.columns];
    // The column whose request did not move is the very same object; the moved one is new.
    expect(after[1]).toBe(before[1]);
    expect(after[1]?.items[0]).toBe(before[1]?.items[0]);
    expect(after[2]).not.toBe(before[2]);
    expect(after[2]?.items[0]?.message).toBe('Now writing the tests.');
    // Free rows too.
    const free = normalizeStructure(fixture.free);
    if (free.kind !== 'ok') throw new Error('expected free');
    const again = normalizeStructure(fixture.free);
    if (again.kind !== 'ok' || again.structure.shape !== 'free' || free.structure.shape !== 'free') throw new Error('free');
    const stable = stabilizeStructure(free.structure, again.structure);
    if (stable.shape !== 'free') throw new Error('free');
    expect(stable.rows[0]).toBe(free.structure.rows[0]);
    expect(stable.rows[1]).toBe(free.structure.rows[1]);
  });
});

describe('phase pages', () => {
  const columns = [column('goal', 'done'), column('interview', 'done', [1, 2]), column('seed', 'current'), column('run', 'pending')];

  it('opens on the current phase', () => {
    expect(currentPageIndex(columns)).toBe(2);
    expect(visiblePage(columns, undefined)).toBe(2);
    // A chosen phase the list no longer has falls back to the current one.
    expect(visiblePage(columns, 'gone')).toBe(2);
    // With no current phase, the newest phase holding a request; with none, the first.
    expect(currentPageIndex([column('a', 'done', [1]), column('b', 'done', [2]), column('c', 'pending')])).toBe(1);
    expect(currentPageIndex([column('a', 'pending'), column('b', 'pending')])).toBe(0);
    expect(visiblePage([], undefined)).toBe(0);
  });

  it('keeps the page the user moved to until they come back to the current one', () => {
    expect(visiblePage(columns, 'goal')).toBe(0);
    expect(visiblePage(columns, 'run')).toBe(3);
    expect(chosenAfterMove(columns, 0)).toBe('goal');
    expect(chosenAfterMove(columns, 3)).toBe('run');
    // Back on the current phase: no choice is held, so the pager follows the phase again.
    expect(chosenAfterMove(columns, 2)).toBeUndefined();
    // Phases that move on under a held choice keep it on its phase, not its index.
    const moved = [column('setup', 'done'), ...columns];
    expect(visiblePage(moved, 'goal')).toBe(1);
    expect(pageAtOffset(780, 390, 4)).toBe(2);
    expect(pageAtOffset(600, 390, 4)).toBe(2);
    expect(pageAtOffset(5_000, 390, 4)).toBe(3);
    expect(pageAtOffset(100, 0, 4)).toBe(0);
  });

  it('labels the page with its place', () => {
    expect(pageLabel(columns, 2)).toBe('seed (3/4)');
    expect(pageLabel(columns, 7)).toBe('');
  });

  it('colours the rail by step', () => {
    expect(phaseRail(columns).map((chip) => chip.look.tone)).toEqual(['done', 'done', 'current', 'pending']);
    expect(phaseRail(columns)[2]?.label).toBe('seed: In progress');
    expect(stepLook('error')).toEqual({ tone: 'err', mark: '✕' });
    expect(stepLook('stopped').tone).toBe('stop');
  });

  it('gives a free request its own small rail', () => {
    const row: StructureRow = {
      item: columns[1]!.items[0]!,
      request: 'done',
      execution: 'current',
      result: 'pending',
    };
    expect(requestRail(row).map((chip) => [chip.id, chip.look.mark])).toEqual([
      ['request', '✓'],
      ['execution', '●'],
      ['result', '○'],
    ]);
  });

  it('names requests and results as the Mac does', () => {
    const item = { ...columns[1]!.items[0]!, title: 'Seed', ordinal: 3 };
    expect(itemHeading(item)).toBe('Seed · Request 3');
    expect(itemHeading({ ...item, title: undefined })).toBe('Request 3');
    expect(resultHeading({ ...item, status: 'error' })).not.toBe(resultHeading(item));
    expect(resultHeading({ ...item, status: 'stopped' })).not.toBe(resultHeading({ ...item, status: 'error' }));
  });
});

describe('folding', () => {
  it('folds a long text at its lines and offers the rest', () => {
    const short = foldText('one\ntwo', FOLD_LINES.request);
    expect(short).toEqual({ preview: 'one\ntwo', folded: false });
    const lines = Array.from({ length: 12 }, (_, index) => `line ${index}`).join('\n');
    const folded = foldText(lines, FOLD_LINES.request);
    expect(folded.folded).toBe(true);
    expect(folded.preview.split('\n')).toHaveLength(FOLD_LINES.request);
    expect(folded.preview.endsWith('…')).toBe(true);
    // One very long line folds too.
    expect(foldText('x'.repeat(FOLD_LINE_CHARS * 3), FOLD_LINES.prompt).folded).toBe(true);
    // Never half an emoji.
    const emoji = foldText(`${'a'.repeat(FOLD_LINE_CHARS * 2 - 1)}😀tail`, FOLD_LINES.prompt);
    expect(emoji.preview).toBe(`${'a'.repeat(FOLD_LINE_CHARS * 2 - 1)}…`);
  });

  it('closes a code fence a folded answer cuts open', () => {
    const fence = '```';
    const answer = ['intro', fence, ...Array.from({ length: 30 }, () => 'code')].join('\n');
    const { preview, folded } = foldText(answer, FOLD_LINES.answer, true);
    expect(folded).toBe(true);
    expect(preview.endsWith(`\n${fence}`)).toBe(true);
    expect(openFence(preview)).toBe(false);
    expect(openFence(`${fence}\ncode`)).toBe(true);
    expect(openFence(`${fence}\ncode\n${fence}`)).toBe(false);
  });

  it('folds a node only when its prompt or output is long', () => {
    const node: StructureNode = { id: 'n', depth: 0, title: 't', status: 'running', kind: 'agent', prompt: 'short' };
    expect(nodeFolds(node)).toBe(false);
    expect(nodeFolds({ ...node, prompt: 'a\nb\nc' })).toBe(true);
    expect(nodeFolds({ ...node, output: Array.from({ length: 7 }, () => 'o').join('\n') })).toBe(true);
    expect(nodeMeta({ ...node, model: 'Opus 5.5' }, (kind) => kind.toUpperCase())).toBe('AGENT · Opus 5.5');
    expect(nodeMeta(node, () => 'Agent')).toBe('Agent');
  });
});

describe('folded requests', () => {
  const item = (runId: string, status: string, finished: boolean, extra: Partial<StructureItem> = {}): StructureItem => ({
    runId,
    ordinal: 1,
    status,
    finished,
    activity: [],
    tally: { agents: 0, tools: 0 },
    clusters: [],
    ...extra,
  });

  it('folds finished requests and keeps running ones open', () => {
    expect(isCardFolded(item('a', 'running', false), new Set())).toBe(false);
    expect(isCardFolded(item('w', 'waiting', false), new Set())).toBe(false);
    // The same request finishes: it folds by itself.
    for (const status of ['completed', 'error', 'stopped']) {
      expect(isCardFolded(item('a', status, true), new Set())).toBe(true);
    }
  });

  it('keeps a card the user opened open until they fold it back', () => {
    const finished = item('a', 'completed', true);
    const opened = toggleCardFold(finished, new Set());
    expect([...opened]).toEqual(['a']);
    expect(isCardFolded(finished, opened)).toBe(false);
    const closed = toggleCardFold(finished, opened);
    expect(closed.size).toBe(0);
    expect(isCardFolded(finished, closed)).toBe(true);
    // A running card has no control; it still folds when it finishes.
    const running = item('b', 'running', false);
    const unchanged = toggleCardFold(running, opened);
    expect(unchanged).toBe(opened);
    expect(isCardFolded(item('b', 'completed', true), unchanged)).toBe(true);
  });

  it('says what was asked and how the request ended on one line each', () => {
    const answered = item('a', 'completed', true, { request: '\n첫 줄 요청\n둘째 줄', answer: '\n## **결론**: 끝났습니다\n- 둘째' });
    expect(foldedRequestLine(answered)).toBe('첫 줄 요청');
    expect(foldedResultLine(answered)).toBe('결론: 끝났습니다');
    expect(foldedResultLine(item('e', 'error', true, { answer: '실패 이유' }))).toBeUndefined();
    expect(foldedResultLine(item('s', 'stopped', true))).toBeUndefined();
    expect(foldedResultLine(item('n', 'completed', true))).toBeUndefined();
    expect(foldedRequestLine(item('r', 'completed', true))).toBe('');
    expect(firstTextLine('```swift\n1. `let` x\n')).toBe('let x');
    expect(firstTextLine('> * 인용 목록')).toBe('인용 목록');
    expect(firstTextLine('  \n\t\n')).toBeUndefined();
  });
});
