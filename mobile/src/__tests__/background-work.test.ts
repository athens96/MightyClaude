import { backgroundElapsed, backgroundLastLine, parseBackgroundWork, shownBlocks, shownNodes } from '@/lib/background-work';

describe('parseBackgroundWork', () => {
  const run = { id: 'w1', kind: 'run', title: 'npm test', status: 'running', startedAt: '2026-10-28T10:00:00Z', command: 'npm test', outputTail: ['a', 'b'] };
  const agent = { id: 'w2', kind: 'agent', title: '조사', status: 'running', startedAt: '2026-10-28T10:00:05Z', command: 'Look into it' };

  it('reads what the host sends', () => {
    expect(parseBackgroundWork({ backgroundWork: [run, agent] })).toEqual([
      { id: 'w1', kind: 'run', title: 'npm test', status: 'running', startedAt: '2026-10-28T10:00:00Z', command: 'npm test', outputTail: ['a', 'b'] },
      { id: 'w2', kind: 'agent', title: '조사', status: 'running', startedAt: '2026-10-28T10:00:05Z', command: 'Look into it' },
    ]);
  });

  it('is empty for a host that predates the field or sends something else', () => {
    expect(parseBackgroundWork(undefined)).toEqual([]);
    expect(parseBackgroundWork({})).toEqual([]);
    expect(parseBackgroundWork({ backgroundWork: 'x' })).toEqual([]);
    expect(parseBackgroundWork({ backgroundWork: { 0: run } })).toEqual([]);
  });

  it('lists running work only, and drops rows it cannot read', () => {
    const rows = parseBackgroundWork({
      backgroundWork: [
        { ...run, id: 'done', status: 'completed' },
        { ...run, id: 'gone', status: 'stopped' },
        { ...run, id: '' },
        { ...run, id: 'odd', kind: 'hologram' },
        { ...run, id: 'num', title: 5 },
        null,
        [],
        'x',
        run,
        { ...run, title: 'again' },
      ],
    });
    expect(rows.map((row) => row.id)).toEqual(['num', 'w1']);
    // A title that is not text reads as empty; a repeated id counts once.
    expect(rows[0]?.title).toBe('');
    expect(rows[1]?.title).toBe('npm test');
  });

  it('keeps text safe and bounded, and never invents an output file', () => {
    const [row] = parseBackgroundWork({
      backgroundWork: [
        { ...run, title: `${'가'.repeat(1_500)}‮`, command: 'x'.repeat(3_000), outputTail: ['1', 2, '3\n4', ...Array.from({ length: 10 }, (_, i) => `l${i}`)], outputFile: '/tmp/secret' },
      ],
    });
    expect(Array.from(row!.title)).toHaveLength(1_000);
    expect(row!.command).toHaveLength(2_000);
    expect(row!.outputTail).toHaveLength(6);
    expect(row!.outputTail?.at(-1)).toBe('l9');
    expect(Object.keys(row!)).not.toContain('outputFile');
  });

  it('omits an empty command and an empty tail', () => {
    const [row] = parseBackgroundWork({ backgroundWork: [{ ...run, command: '  ', outputTail: ['', '  '] }] });
    expect(row).toEqual({ id: 'w1', kind: 'run', title: 'npm test', status: 'running', startedAt: '2026-10-28T10:00:00Z' });
  });

  it('caps how many rows it keeps', () => {
    const many = Array.from({ length: 80 }, (_, i) => ({ ...run, id: `w${i}` }));
    expect(parseBackgroundWork({ backgroundWork: many })).toHaveLength(50);
  });
});

describe('elapsed and last line', () => {
  it('counts from the start the host reported, and gives nothing for a time it cannot read', () => {
    const start = Date.parse('2026-10-28T10:00:00Z');
    expect(backgroundElapsed('2026-10-28T10:00:00Z', start + 65_000)).toBe(65_000);
    expect(backgroundElapsed('2026-10-28T10:00:00Z', start - 5)).toBe(0);
    expect(backgroundElapsed('', start)).toBeUndefined();
    expect(backgroundElapsed('soon', start)).toBeUndefined();
  });

  it('shows the last line that has something on it', () => {
    expect(backgroundLastLine(['a', ' b ', '', '  '])).toBe('b');
    expect(backgroundLastLine([])).toBe('');
    expect(backgroundLastLine(undefined)).toBe('');
  });
});

describe('what a run draws', () => {
  const blocks = [{ id: 'a' }, { id: 'b', background: true as const }, { id: 'c' }];

  it('omits background blocks unless the run is a notice run', () => {
    expect(shownBlocks(blocks, false).map((block) => block.id)).toEqual(['a', 'c']);
    expect(shownBlocks(blocks, true).map((block) => block.id)).toEqual(['a', 'b', 'c']);
    expect(shownBlocks([], false)).toEqual([]);
  });

  it('takes the nodes under a hidden background node with it, until the depth comes back', () => {
    const nodes = [
      { id: 'a', depth: 0 },
      { id: 'bg', depth: 1, background: true as const },
      { id: 'bg-child', depth: 2 },
      { id: 'bg-grandchild', depth: 3 },
      { id: 'sibling', depth: 1 },
      { id: 'bg2', depth: 0, background: true as const },
      { id: 'bg2-child', depth: 1 },
      { id: 'b', depth: 0 },
    ];
    expect(shownNodes(nodes, false).map((node) => node.id)).toEqual(['a', 'sibling', 'b']);
    expect(shownNodes(nodes, true)).toHaveLength(nodes.length);
  });
});
