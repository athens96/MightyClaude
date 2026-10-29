import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { normalizeMighty } from '@/lib/mighty';
import { normalizeStructure } from '@/lib/structure';
import {
  BACKGROUND_OUTPUT,
  backgroundEnded,
  backgroundLabel,
  parseBackground,
  shownTail,
} from '@/lib/background-output';
import { resetLanguage } from '@/lib/i18n';

// The assertions read the Korean copy: pin it, whatever the machine's language.
beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/background-output.json'), 'utf8'),
) as { maxLines: number; cases: { name: string; lines: string[] }[] };

describe('the shared background output contract', () => {
  it('keeps the phone on the number of lines the hosts cut to', () => {
    expect(BACKGROUND_OUTPUT.maxLines).toBe(fixture.maxLines);
  });

  it('shows every fixture tail the host would send, whole, when opened', () => {
    for (const each of fixture.cases) {
      expect(each.lines.length).toBeLessThanOrEqual(BACKGROUND_OUTPUT.maxLines);
      expect(parseBackground({ outputTail: each.lines }, 100).outputTail ?? []).toEqual(
        each.lines.some((line) => line.trim().length > 0) ? each.lines : [],
      );
      expect(shownTail(each.lines, 'running', true)).toEqual(each.lines);
    }
  });
});

describe('backgroundLabel', () => {
  it('says a running task waits for its notice', () => {
    expect(backgroundLabel({ kind: 'task', background: true, status: 'running' })).toBe('백그라운드 실행 · 실행 중');
  });

  it('says how an ended task ended', () => {
    expect(backgroundLabel({ kind: 'task', background: true, status: 'completed' })).toBe('백그라운드 실행 · 완료');
    expect(backgroundLabel({ kind: 'task', background: true, status: 'error' })).toBe('백그라운드 실행 · 실패');
    expect(backgroundLabel({ kind: 'task', status: 'interrupted' })).toBe('백그라운드 실행 · 중단됨');
    expect(backgroundLabel({ kind: 'task', status: 'unknown' })).toBe('백그라운드 실행 · 알 수 없음');
  });

  it('names a background sub-agent, waiting or ended', () => {
    expect(backgroundLabel({ kind: 'agent', background: true, status: 'running' })).toBe('하위 에이전트 · 응답 대기');
    expect(backgroundLabel({ kind: 'agent', background: true, status: 'cancelled' })).toBe('하위 에이전트 · 중단됨');
  });

  it('says nothing of a foreground agent, a steer or a block that is no background work', () => {
    expect(backgroundLabel({ kind: 'agent', status: 'running' })).toBeNull();
    expect(backgroundLabel({ kind: 'steer', background: true, status: 'completed' })).toBeNull();
    expect(backgroundLabel({ kind: 'main', status: 'completed' })).toBeNull();
  });

  it('counts unknown as no end', () => {
    expect(backgroundEnded('failed')).toBe(true);
    expect(backgroundEnded('unknown')).toBe(false);
    expect(backgroundEnded('running')).toBe(false);
  });
});

describe('shownTail', () => {
  const lines = ['1', '2', '3', '4', '5', '6'];

  it('keeps three lines while running, two once ended and all six opened', () => {
    expect(shownTail(lines, 'running', false)).toEqual(['4', '5', '6']);
    expect(shownTail(lines, 'completed', false)).toEqual(['5', '6']);
    expect(shownTail(lines, 'completed', true)).toEqual(lines);
    expect(shownTail(undefined, 'running', false)).toEqual([]);
  });
});

describe('parseBackground', () => {
  it('reads nothing from a host that predates the fields', () => {
    expect(parseBackground({}, 100)).toEqual({});
  });

  it('keeps the flag, at most six lines and a clipped reply', () => {
    const read = parseBackground(
      { background: true, outputTail: ['0', '1', '2', '3', '4', '5', '6', 7], noticeReply: 'x'.repeat(300) },
      100,
    );
    expect(read.background).toBe(true);
    expect(read.outputTail).toEqual(['1', '2', '3', '4', '5', '6']);
    expect(read.noticeReply).toHaveLength(100);
  });

  it('ignores a flag that is not true and a tail of nothing', () => {
    expect(parseBackground({ background: 'yes', outputTail: ['', '  '], noticeReply: '  ' }, 100)).toEqual({});
  });
});

describe('the fields on a payload', () => {
  it('reach a Mighty block', () => {
    const mighty = normalizeMighty({
      runs: [
        {
          id: 'r1:notice:1',
          noticeRun: true,
          input: 'go',
          status: 'running',
          blocks: [{ id: 'b1', kind: 'task', title: 'build', status: 'running', background: true, outputTail: ['a', 'b'] }],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]).toMatchObject({ background: true, outputTail: ['a', 'b'] });
  });

  it('reach a structure node', () => {
    const structure = normalizeStructure({
      v: 1,
      shape: 'free',
      rows: [
        {
          request: 'done',
          execution: 'done',
          result: 'done',
          item: {
            runId: 'r1:notice:1',
            noticeRun: true,
            ordinal: 1,
            status: 'completed',
            finished: true,
            clusters: [
              { nodes: [{ id: 'n1', depth: 0, title: 't', status: 'completed', kind: 'task', background: true, outputTail: ['x'], noticeReply: 'ok' }] },
            ],
          },
        },
      ],
    });
    if (structure.kind !== 'ok' || structure.structure.shape !== 'free') throw new Error('expected free');
    expect(structure.structure.rows[0]?.item.clusters[0]?.nodes[0]).toMatchObject({
      background: true,
      outputTail: ['x'],
      noticeReply: 'ok',
    });
  });
});
