import type { MobileBlock, MobileMightyRun } from '@/api/types';
import {
  MAX_BLOCKS_PER_RUN,
  MAX_INPUT_PREVIEW,
  blockKindLabel,
  blockKindMark,
  blockTitle,
  guidedRequestFor,
  guidedStyleOf,
  normalizeMighty,
  ouroborosPhaseLabel,
  runHeading,
  runPreview,
} from '@/lib/mighty';

function block(overrides: Partial<MobileBlock> = {}): MobileBlock {
  return { id: 'b1', kind: 'agent', title: '', status: 'running', ...overrides };
}

function run(overrides: Partial<MobileMightyRun> = {}): MobileMightyRun {
  return { id: 'r1', input: '고쳐 줘', status: 'completed', blocks: [], ...overrides };
}

describe('block labels and marks', () => {
  it('names the six kinds the Mac draws', () => {
    expect(blockKindLabel('main')).toBe('요청');
    expect(blockKindLabel('agent')).toBe('하위 에이전트');
    expect(blockKindLabel('task')).toBe('백그라운드 작업');
    expect(blockKindLabel('steer')).toBe('중간 요청');
    expect(blockKindLabel('compact')).toBe('컨텍스트 정리');
    expect(blockKindLabel('question')).toBe('질문');
  });

  it('shows a kind it does not know instead of guessing one', () => {
    expect(blockKindLabel('hologram')).toBe('hologram');
    expect(blockKindLabel('')).toBe('블록');
    expect(blockKindMark('hologram')).toBe(blockKindMark('also-unknown'));
    expect(blockKindMark('hologram')).not.toBe(blockKindMark('main'));
  });

  it('falls back from an empty block title to what its kind is called', () => {
    expect(blockTitle(block({ title: '  검색  ' }))).toBe('검색');
    expect(blockTitle(block({ title: '   ' }))).toBe('하위 에이전트');
    expect(blockTitle(block({ title: '', kind: 'nope' }))).toBe('nope');
  });
});

describe('run headings and previews', () => {
  it('prefixes the title and counts requests from one', () => {
    expect(runHeading(run({ title: '인터뷰' }), 0)).toBe('인터뷰 · 요청 1');
    expect(runHeading(run(), 2)).toBe('요청 3');
    expect(runHeading(run({ title: '   ' }), 1)).toBe('요청 2');
  });

  it('flattens a multi-line input into one line and cuts it', () => {
    expect(runPreview('첫 줄\n둘째  줄')).toBe('첫 줄 둘째 줄');
    const long = 'ㄱ'.repeat(MAX_INPUT_PREVIEW + 10);
    expect(runPreview(long)).toHaveLength(MAX_INPUT_PREVIEW + 1);
    expect(runPreview(long).endsWith('…')).toBe(true);
  });
});

describe('ouroborosPhaseLabel', () => {
  it('uses the Mac wording and passes an unknown phase through', () => {
    expect(ouroborosPhaseLabel('interview')).toBe('인터뷰');
    expect(ouroborosPhaseLabel('evolve')).toBe('진화');
    expect(ouroborosPhaseLabel('transcend')).toBe('transcend');
    expect(ouroborosPhaseLabel('')).toBe('단계');
  });
});

describe('guidedRequestFor', () => {
  it('carries the composer text only for the skills the host listed', () => {
    expect(guidedRequestFor('ouroboros', 'interview', '  로그인 화면  ', ['interview'])).toEqual({
      style: 'ouroboros',
      skill: 'interview',
      text: '로그인 화면',
    });
    expect(guidedRequestFor('ouroboros', 'seed', '로그인 화면', ['interview'])).toEqual({
      style: 'ouroboros',
      skill: 'seed',
    });
    expect(guidedRequestFor('ouroboros', 'interview', '   ', ['interview'])).toEqual({
      style: 'ouroboros',
      skill: 'interview',
    });
  });

  it('always offers the text to Paperthin, whose skills take a target', () => {
    expect(guidedRequestFor('paperthin', 're0', 'docs/spec.md', [])).toEqual({
      style: 'paperthin',
      skill: 're0',
      text: 'docs/spec.md',
    });
    expect(guidedRequestFor('paperthin', 'nba', '', [])).toEqual({
      style: 'paperthin',
      skill: 'nba',
    });
  });
});

describe('normalizeMighty', () => {
  it('reads the documented payload', () => {
    const mighty = normalizeMighty({
      style: 'ouroboros',
      runs: [
        {
          id: 'r1',
          input: '로그인 고쳐 줘',
          title: '인터뷰',
          status: 'completed',
          blocks: [
            {
              id: 'b1',
              kind: 'main',
              title: '요청',
              status: 'completed',
              summary: '두 파일 수정',
              output: 'diff…',
              durationMs: 1200,
            },
          ],
        },
      ],
      ouroboros: {
        phase: 'interview',
        ready: false,
        takesText: ['interview', 'auto'],
        next: [{ skill: 'seed', title: '시드 생성', help: '명세로 굳힙니다' }],
        all: [{ skill: 'status', title: '상태', help: '' }],
      },
    });
    expect(mighty?.style).toBe('ouroboros');
    expect(mighty?.runs[0]?.blocks[0]).toEqual({
      id: 'b1',
      kind: 'main',
      title: '요청',
      status: 'completed',
      summary: '두 파일 수정',
      output: 'diff…',
      durationMs: 1200,
    });
    expect(mighty?.ouroboros?.ready).toBe(false);
    expect(mighty?.ouroboros?.takesText).toEqual(['interview', 'auto']);
    expect(mighty?.paperthin).toBeUndefined();
  });

  it('reads the Paperthin map, skills and casebook', () => {
    const mighty = normalizeMighty({
      style: 'paperthin',
      runs: [],
      paperthin: {
        installed: false,
        recommended: 're0-memo',
        domains: [
          {
            id: 'coil',
            title: 'coil',
            axis: '하나 · 반복',
            question: '각 패스가 다음 패스를 가르쳤는가?',
            skills: [
              {
                name: 're0-memo',
                emoji: '🧭',
                summary: '교훈을 뽑아냅니다',
                scope: '완료된 사이클 하나',
                userInvoked: false,
                readOnly: true,
              },
            ],
          },
        ],
        casebook: { name: '2026-09-cycle', weight: 'full', files: ['DESIGN.md', 'EVIDENCE.md'] },
      },
    });
    expect(mighty?.paperthin?.installed).toBe(false);
    expect(mighty?.paperthin?.recommended).toBe('re0-memo');
    expect(mighty?.paperthin?.domains[0]?.skills[0]?.readOnly).toBe(true);
    expect(mighty?.paperthin?.casebook?.files).toEqual(['DESIGN.md', 'EVIDENCE.md']);
  });

  it('drops entries it cannot draw instead of throwing', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        null,
        { input: 'no id' },
        { id: 'r1', blocks: [{ kind: 'main' }, 7, { id: 'b2', kind: 'wormhole' }] },
      ],
      ouroboros: 'not an object',
      paperthin: { domains: [{ title: 'no id' }] },
    });
    expect(mighty?.runs).toHaveLength(1);
    expect(mighty?.runs[0]?.blocks).toEqual([{ id: 'b2', kind: 'wormhole', title: '', status: '' }]);
    expect(mighty?.runs[0]?.input).toBe('');
    expect(mighty?.ouroboros).toBeUndefined();
    expect(mighty?.paperthin?.domains).toEqual([]);
    expect(normalizeMighty(undefined)).toBeUndefined();
    expect(normalizeMighty([])).toBeUndefined();
  });

  it('keeps only the newest twenty runs and clamps a long output', () => {
    const runs = Array.from({ length: 25 }, (_, index) => ({
      id: `r${index}`,
      input: '',
      status: 'completed',
      blocks: [{ id: `b${index}`, kind: 'main', title: '', status: '', output: 'x'.repeat(3000) }],
    }));
    const mighty = normalizeMighty({ style: 'cli', runs });
    expect(mighty?.runs).toHaveLength(20);
    expect(mighty?.runs[0]?.id).toBe('r5');
    expect(mighty?.runs[0]?.blocks[0]?.output).toHaveLength(2000);
  });

  it('keeps only the newest two hundred blocks of a run and counts the rest', () => {
    const blocks = Array.from({ length: 260 }, (_, index) => ({
      id: `b${index}`,
      kind: 'agent',
      title: `블록 ${index}`,
      status: 'completed',
    }));
    const mighty = normalizeMighty({ style: 'cli', runs: [{ id: 'r1', input: '', blocks }] });
    const run = mighty?.runs[0];

    expect(run?.blocks).toHaveLength(MAX_BLOCKS_PER_RUN);
    expect(run?.blocks[0]?.id).toBe('b60');
    expect(run?.blocks.at(-1)?.id).toBe('b259');
    expect(run?.omittedBlocks).toBe(60);
  });

  it('says nothing about omitted blocks when every one of them is there', () => {
    const blocks = Array.from({ length: MAX_BLOCKS_PER_RUN }, (_, index) => ({
      id: `b${index}`,
      kind: 'agent',
      status: 'completed',
    }));
    const mighty = normalizeMighty({ style: 'cli', runs: [{ id: 'r1', input: '', blocks }] });
    expect(mighty?.runs[0]?.blocks).toHaveLength(MAX_BLOCKS_PER_RUN);
    expect(mighty?.runs[0]?.omittedBlocks).toBeUndefined();
  });

  it('strips control and bidi characters from what it will draw', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        {
          id: 'r1',
          input: '',
          status: '',
          blocks: [
            {
              id: 'b1',
              kind: 'main',
              title: '\u202egnitset\u202c',
              status: '',
              summary: 'a\u0007b',
              output: 'first\nsecond\u202e',
            },
          ],
        },
      ],
    });
    const parsed = mighty?.runs[0]?.blocks[0];
    expect(parsed?.title).toBe('gnitset');
    expect(parsed?.summary).toBe('ab');
    // Newlines survive: block output is shown as a monospace excerpt.
    expect(parsed?.output).toBe('first\nsecond');
  });
});

describe('guidedStyleOf', () => {
  it('only knows the two guided styles', () => {
    expect(guidedStyleOf({ style: 'ouroboros', runs: [] })).toBe('ouroboros');
    expect(guidedStyleOf({ style: 'paperthin', runs: [] })).toBe('paperthin');
    expect(guidedStyleOf({ style: 'cli', runs: [] })).toBeUndefined();
    expect(guidedStyleOf(undefined)).toBeUndefined();
  });
});
