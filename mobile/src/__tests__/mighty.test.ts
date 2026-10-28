import {
  MAX_BLOCK_ACTIVITY,
  MAX_BLOCK_SUMMARY,
  MAX_RUN_RESULT,
  type MobileBlock,
  type MobileMightyRun,
} from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import {
  MAX_BLOCKS_PER_RUN,
  MAX_INPUT_PREVIEW,
  RESULT_PREVIEW_CHARS,
  RESULT_PREVIEW_LINES,
  blockActivity,
  blockGist,
  blockKindLabel,
  blockPrompt,
  blockKindMark,
  blockTitle,
  defaultView,
  followRunIndex,
  isQuietNotice,
  newestRequestIndex,
  normalizeMighty,
  requestOrdinals,
  resultPreview,
  runHeading,
  runPreview,
  runFold,
  runResult,
} from '@/lib/mighty';
import { panelOf } from '@/lib/styles';

// These assertions read the Korean copy: pin it, whatever the machine's language.
beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

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

  it('names a steer that delegation sent 위임 알림 and one a person sent 중간 요청', () => {
    const notice = '[Mighty Claude notice n-1] Your child c-1 reported revision 1.';
    const followUp = '[Mighty Claude follow-up f-1] Your parent sent you this follow-up:';
    // An older host still titles a notice 중간 요청; the phone names it from what it was asked.
    expect(blockTitle(block({ kind: 'steer', title: '중간 요청', summary: notice }))).toBe('위임 알림');
    expect(blockTitle(block({ kind: 'steer', title: '중간 요청', summary: followUp }))).toBe('위임 알림');
    expect(blockKindLabel('steer', notice)).toBe('위임 알림');
    expect(blockTitle(block({ kind: 'steer', title: '중간 요청', summary: 'Also add tests' }))).toBe('중간 요청');
    expect(blockKindLabel('steer', 'Also add tests')).toBe('중간 요청');
    expect(blockKindLabel('steer')).toBe('중간 요청');
    // Only a steer is one: a sub-agent asked with the same words keeps its own title.
    expect(blockTitle(block({ kind: 'agent', title: '검색', summary: notice }))).toBe('검색');
  });
});

describe('run headings and previews', () => {
  it('prefixes the title and counts requests from one', () => {
    expect(runHeading(run({ title: '인터뷰' }), 1)).toBe('인터뷰 · 요청 1');
    expect(runHeading(run(), 3)).toBe('요청 3');
    expect(runHeading(run({ title: '   ' }), 2)).toBe('요청 2');
  });

  it('flattens a multi-line input into one line and cuts it', () => {
    expect(runPreview('첫 줄\n둘째  줄')).toBe('첫 줄 둘째 줄');
    const long = 'ㄱ'.repeat(MAX_INPUT_PREVIEW + 10);
    expect(runPreview(long)).toHaveLength(MAX_INPUT_PREVIEW + 1);
    expect(runPreview(long).endsWith('…')).toBe(true);
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
  });

  it('ignores the Paperthin payload a host from before v7 still sends', () => {
    // The style and its legacy payload were removed in v7 (contract 7.4). An older host
    // can still send both; the id is kept as it arrived and the payload is dropped whole.
    const mighty = normalizeMighty({
      style: 'paperthin',
      runs: [],
      paperthin: {
        installed: true,
        domains: [{ id: 'coil', title: 'coil', skills: [{ name: 're0-memo' }] }],
      },
    });
    expect(mighty).toEqual({ style: 'paperthin', runs: [] });
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
    });
    expect(mighty?.runs).toHaveLength(1);
    expect(mighty?.runs[0]?.blocks).toEqual([{ id: 'b2', kind: 'wormhole', title: '', status: '' }]);
    expect(mighty?.runs[0]?.input).toBe('');
    expect(mighty?.ouroboros).toBeUndefined();
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

describe('which panes draw a guided panel', () => {
  it('reads the style id and panel a host with "style" sends', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      styleId: 'gstack',
      runs: [],
      panel: {
        style: { id: 'gstack', name: 'gstack', source: 'workspace' },
        groups: [],
        actions: [{ id: 'ship', title: '출시', help: '', takesText: false, requiresText: false }],
        next: ['ship'],
        attachments: [],
        setup: { ready: true, missing: [] },
        presentation: { headerTitle: 'gstack', source: 'workspace' },
      },
    });
    expect(mighty?.styleId).toBe('gstack');
    // `style` stays the fixed wire word; the truth rides in `styleId` (contract 7.2).
    expect(mighty?.style).toBe('cli');
    expect(mighty?.panel?.style.id).toBe('gstack');
  });

  it('draws any style the host sends a panel for, not a set of ids it knows', () => {
    const drawn = (raw: unknown) => panelOf(normalizeMighty(raw))?.style.id;
    const panel = (id: string) => ({
      style: { id, name: id, source: 'user' },
      groups: [],
      actions: [],
      next: [],
      attachments: [],
      setup: { ready: true, missing: [] },
      presentation: { headerTitle: id, source: 'user' },
    });
    expect(drawn({ style: 'cli', runs: [], panel: panel('oh-my-claudecode') })).toBe(
      'oh-my-claudecode',
    );
    expect(drawn({ style: 'cli', runs: [], panel: panel('style-nobody-has-seen') })).toBe(
      'style-nobody-has-seen',
    );
  });

  it('falls back to the built-in payloads, and to blocks alone without either', () => {
    const legacy = {
      style: 'ouroboros',
      runs: [],
      ouroboros: {
        phase: 'seed',
        ready: true,
        takesText: [],
        next: [{ skill: 'seed', title: '시드', help: '' }],
        all: [{ skill: 'seed', title: '시드', help: '' }],
      },
    };
    // A host that never learned "style" sends no panel; the adapter makes one.
    expect(panelOf(normalizeMighty(legacy), false)?.style.id).toBe('ouroboros');
    expect(panelOf(normalizeMighty({ style: 'cli', runs: [] }))).toBeUndefined();
    expect(panelOf(normalizeMighty({ style: 'gstack', runs: [] }))).toBeUndefined();
    expect(panelOf(undefined)).toBeUndefined();
  });

  it('keeps a panel it could not read apart from a pane that has none', () => {
    // No panel at all is a CLI pane; a panel that arrived unreadable is a host change
    // this app does not know about, and the screen says so instead of quietly dropping
    // to the legacy payload.
    const unreadable = normalizeMighty({ style: 'cli', styleId: 'gstack', runs: [], panel: 7 });
    expect(unreadable?.panel).toBeUndefined();
    expect(unreadable?.panelUnreadable).toBe(true);
    expect(panelOf(unreadable, true)?.setup.ready).toBe(false);
    expect(normalizeMighty({ style: 'cli', runs: [] })?.panelUnreadable).toBeUndefined();
  });
});

describe('model labels on blocks', () => {
  it('preserves nodeModelLabel on the main block when the Mac projected a confirmed model', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        {
          id: 'r1',
          input: '고쳐 줘',
          status: 'completed',
          blocks: [{ id: 'b1', kind: 'main', title: '요청 1', status: 'completed', nodeModelLabel: 'claude-sonnet-5' }],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]?.nodeModelLabel).toBe('claude-sonnet-5');
  });

  it('preserves a configured-name label (with settings marker) from the Mac', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        {
          id: 'r1',
          input: '고쳐 줘',
          status: 'completed',
          blocks: [
            { id: 'b1', kind: 'main', title: '요청 1', status: 'completed', nodeModelLabel: 'claude-opus-5 · 설정' },
          ],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]?.nodeModelLabel).toBe('claude-opus-5 · 설정');
  });

  it('leaves nodeModelLabel absent when the block has none', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        {
          id: 'r1',
          input: '고쳐 줘',
          status: 'completed',
          blocks: [{ id: 'b1', kind: 'main', title: '요청 1', status: 'completed' }],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]?.nodeModelLabel).toBeUndefined();
  });

  it('leaves nodeModelLabel absent when the Mac sent default (no label)', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        {
          id: 'r1',
          input: '고쳐 줘',
          status: 'completed',
          blocks: [{ id: 'b1', kind: 'main', title: '요청 1', status: 'completed', nodeModelLabel: null }],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]?.nodeModelLabel).toBeUndefined();
  });

  it('strips unsafe characters from the model label', () => {
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
              title: '',
              status: '',
              nodeModelLabel: 'claude‮-sonnet‬-5',
            },
          ],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]?.nodeModelLabel).toBe('claude-sonnet-5');
  });

  it('shows the label on the main block and not on child blocks', () => {
    const mighty = normalizeMighty({
      style: 'cli',
      runs: [
        {
          id: 'r1',
          input: '',
          status: 'completed',
          blocks: [
            { id: 'b1', kind: 'main', title: '요청 1', status: 'completed', nodeModelLabel: 'claude-sonnet-5' },
            { id: 'b2', kind: 'agent', title: '하위 에이전트', status: 'completed' },
          ],
        },
      ],
    });
    expect(mighty?.runs[0]?.blocks[0]?.nodeModelLabel).toBe('claude-sonnet-5');
    expect(mighty?.runs[0]?.blocks[1]?.nodeModelLabel).toBeUndefined();
  });
});

describe('default session view', () => {
  it('opens blocks when the pane carries a mighty payload', () => {
    const mighty = normalizeMighty({ style: 'cli', runs: [] });
    expect(defaultView(mighty)).toBe('blocks');
  });

  it('opens log when no mighty payload is present (shell pane)', () => {
    expect(defaultView(undefined)).toBe('log');
  });

  it('opens blocks regardless of what Mac view mode the payload came from', () => {
    // The Mac pane may be in plain/transcript view; the phone still opens blocks.
    const mighty = normalizeMighty({ style: 'cli', runs: [], agentViewMode: 'plain' });
    expect(defaultView(mighty)).toBe('blocks');
  });
});

describe('block labels keyed by what the host sent', () => {
  it('reads a prototype key as an unknown kind, not as a function', () => {
    // A plain object answers `constructor` with a function and `__proto__` with an
    // object, either of which would reach `<Text>` as something that is not a string.
    for (const key of ['__proto__', 'constructor', 'toString', 'valueOf']) {
      expect({ key, label: blockKindLabel(key) }).toEqual({ key, label: key });
      expect({ key, mark: blockKindMark(key) }).toEqual({ key, mark: blockKindMark('hologram') });
    }
  });
});

describe('what a block says folded and open', () => {
  it('shows the latest step while running, then the first line of the result, then the prompt', () => {
    const running = block({ summary: '버그 찾기', activity: ['a.swift 읽기', 'grep foo'], output: '중간' });
    expect(blockGist(running, '')).toBe('grep foo');
    const waiting = block({ status: 'waiting', activity: ['질문 대기'] });
    expect(blockGist(waiting, '')).toBe('질문 대기');
    // Settled: stale steps no longer speak, the result does.
    const done = block({ status: 'completed', summary: '버그 찾기', activity: ['grep foo'], output: '\n\n  고쳤습니다\n자세히' });
    expect(blockGist(done, '')).toBe('고쳤습니다');
    expect(blockGist(block({ status: 'completed', summary: '버그 찾기' }), '')).toBe('버그 찾기');
    expect(blockGist(block({ summary: '버그 찾기' }), '')).toBe('버그 찾기');
    expect(blockGist(block(), '')).toBe('');
  });

  it('reads the request block from the run input, even from a Mac that sends no summary', () => {
    const main = block({ kind: 'main', status: 'completed' });
    expect(blockPrompt(main, '  첫 줄\n둘째 줄  ')).toBe('첫 줄\n둘째 줄');
    expect(blockGist(main, '첫 줄\n둘째 줄')).toBe('첫 줄 둘째 줄');
    expect(blockPrompt(block({ kind: 'main', summary: '요약' }), '')).toBe('요약');
    // A child block's prompt is its own summary, never the run's request.
    expect(blockPrompt(block({ summary: '찾아 줘' }), '고쳐 줘')).toBe('찾아 줘');
    const long = 'ㄱ'.repeat(MAX_INPUT_PREVIEW + 5);
    expect(blockGist(block({ activity: [long] }), '')).toHaveLength(MAX_INPUT_PREVIEW + 1);
  });

  it('lists steps only while the block is in motion', () => {
    expect(blockActivity(block({ activity: ['a', 'b'] }))).toEqual(['a', 'b']);
    expect(blockActivity(block({ status: 'completed', activity: ['a'] }))).toEqual([]);
    expect(blockActivity(block())).toEqual([]);
  });

  it('parses activity lines defensively and keeps the newest', () => {
    const lines = Array.from({ length: MAX_BLOCK_ACTIVITY + 3 }, (_, index) => `단계 ${index}`);
    const parse = (activity: unknown, summary?: unknown) =>
      normalizeMighty({
        style: 'cli',
        runs: [{ id: 'r1', input: '', status: 'running', blocks: [{ id: 'b1', kind: 'agent', title: '', status: 'running', activity, summary }] }],
      })?.runs[0]?.blocks[0];
    expect(parse(lines)?.activity).toEqual(lines.slice(-MAX_BLOCK_ACTIVITY));
    expect(parse(['a\u0007b', '', 3, null, '  c  '])?.activity).toEqual(['ab', 'c']);
    expect(parse('not a list')?.activity).toBeUndefined();
    expect(parse([])?.activity).toBeUndefined();
    // An old Mac sends none at all; the summary keeps the full prompt the Mac allows.
    expect(parse(undefined, 'ㄱ'.repeat(MAX_BLOCK_SUMMARY + 5))?.summary).toHaveLength(MAX_BLOCK_SUMMARY);
  });
});

describe('how a request opens in the Mighty view', () => {
  const main = (output?: string) => block({ id: 'm', kind: 'main', status: 'completed', output });

  it('shows a finished request with its result and its steps folded, newest or not', () => {
    for (const newest of [true, false]) {
      expect(runFold(run({ result: '답', blocks: [main('답')] }), newest)).toEqual({ open: true, stepsOpen: false });
      expect(runFold(run({ status: 'error', blocks: [main('오류 설명')] }), newest)).toEqual({ open: true, stepsOpen: false });
    }
  });

  it('shows the steps of a request with no result yet, and opens only the newest', () => {
    expect(runFold(run({ status: 'running', blocks: [main('중간')] }), true)).toEqual({ open: true, stepsOpen: true });
    expect(runFold(run({ status: 'running', blocks: [main('중간')] }), false)).toEqual({ open: false, stepsOpen: true });
    // A finished request that said nothing has only its steps to show.
    expect(runFold(run({ blocks: [main()] }), true)).toEqual({ open: true, stepsOpen: true });
  });
});

describe('the final result of a run', () => {
  const main = (output?: string) => block({ id: 'm', kind: 'main', status: 'completed', output });

  it('prefers the full result, then falls back to the request block output', () => {
    expect(runResult(run({ result: '# 전체 답', blocks: [main('짧은 답')] }))).toBe('# 전체 답');
    // An older run, or an older Mac, has only the request block's output.
    expect(runResult(run({ blocks: [block({ output: '하위 답' }), main('짧은 답')] }))).toBe('짧은 답');
    expect(runResult(run({ status: 'error', blocks: [main('오류 설명')] }))).toBe('오류 설명');
    expect(runResult(run({ status: 'stopped', result: '멈춘 곳까지' }))).toBe('멈춘 곳까지');
  });

  it('has nothing to show while running or when the run said nothing', () => {
    expect(runResult(run({ status: 'running', result: '중간', blocks: [main('중간')] }))).toBeUndefined();
    expect(runResult(run({ status: 'waiting', blocks: [main('중간')] }))).toBeUndefined();
    expect(runResult(run({ status: 'hologram', result: '답' }))).toBeUndefined();
    expect(runResult(run({ blocks: [main()] }))).toBeUndefined();
    expect(runResult(run({ blocks: [main('  \n ')] }))).toBeUndefined();
    // A child block's answer is never mistaken for the run's own.
    expect(runResult(run({ blocks: [block({ status: 'completed', output: '하위 답' })] }))).toBeUndefined();
    expect(runResult(run())).toBeUndefined();
  });

  it('parses the result defensively and clamps it', () => {
    const parse = (result: unknown) =>
      normalizeMighty({ style: 'cli', runs: [{ id: 'r1', input: '', status: 'completed', blocks: [], result }] })
        ?.runs[0]?.result;
    expect(parse('x'.repeat(MAX_RUN_RESULT + 10))).toHaveLength(MAX_RUN_RESULT);
    expect(parse('답\u0007변')).toBe('답변');
    expect(parse('   ')).toBeUndefined();
    expect(parse(42)).toBeUndefined();
    expect(parse(undefined)).toBeUndefined();
  });

  it('shows the head of a long result and says there is more', () => {
    const lines = Array.from({ length: RESULT_PREVIEW_LINES + 5 }, (_, index) => `줄 ${index}`).join('\n');
    const long = resultPreview(lines);
    expect(long.folded).toBe(true);
    expect(long.preview.split('\n')).toHaveLength(RESULT_PREVIEW_LINES);
    expect(long.preview.endsWith('줄 14…')).toBe(true);
    const wide = resultPreview('가'.repeat(RESULT_PREVIEW_CHARS * 2));
    expect(wide.folded).toBe(true);
    expect(wide.preview).toHaveLength(RESULT_PREVIEW_CHARS + 1);
    // An emoji cut in half at the limit is dropped rather than left broken.
    const emoji = resultPreview(`${'가'.repeat(RESULT_PREVIEW_CHARS - 1)}😀 끝`);
    expect(emoji.preview).toBe(`${'가'.repeat(RESULT_PREVIEW_CHARS - 1)}…`);
    // A short result, even with trailing blank lines, is shown whole.
    expect(resultPreview('짧은 답\n\n')).toEqual({ preview: '짧은 답\n\n', folded: false });
  });
});


describe('notice runs: Claude\'s reply to finished background work', () => {
  const main = (output?: string, status = 'completed') => block({ id: 'm', kind: 'main', status, output });
  const notice = (overrides: Partial<MobileMightyRun> = {}) => run({ id: 'r1:notice:1', noticeRun: true, blocks: [main('정리했습니다')], ...overrides });

  it('is no request: it carries its own name and takes no request number', () => {
    const runs = [run({ id: 'a' }), notice(), run({ id: 'b' }), notice({ id: 'b:notice:1' })];
    expect(requestOrdinals(runs)).toEqual([1, 1, 2, 2]);
    expect(runHeading(runs[1]!, 1)).toBe('백그라운드 작업 완료');
    expect(runHeading(runs[2]!, 2)).toBe('요청 2');
    // A pane that starts with a notice run never shows 0.
    expect(requestOrdinals([notice(), run()])).toEqual([1, 1]);
    expect(newestRequestIndex(runs)).toBe(2);
    expect(newestRequestIndex([notice()])).toBeUndefined();
  });

  it('is followed only when it is a turn of its own, never when it was taken into a running turn', () => {
    const quiet = notice({ blocks: [main()] });
    expect(isQuietNotice(quiet)).toBe(true);
    expect(isQuietNotice(notice())).toBe(false);
    expect(isQuietNotice(notice({ status: 'error', blocks: [main()] }))).toBe(false);
    expect(isQuietNotice(notice({ status: 'running', blocks: [main(undefined, 'running')] }))).toBe(false);
    expect(isQuietNotice(run({ blocks: [main()] }))).toBe(false);
    expect(followRunIndex([run({ id: 'a' }), quiet])).toBe(0);
    expect(followRunIndex([run({ id: 'a' }), notice()])).toBe(1);
    expect(followRunIndex([run({ id: 'a' }), notice({ status: 'running', blocks: [main(undefined, 'running')] })])).toBe(1);
    // The newest request stays the one followed when it comes after the notice.
    expect(followRunIndex([notice(), run({ id: 'b' })])).toBe(1);
    expect(followRunIndex([])).toBeUndefined();
  });

  it('has no result card when quiet, and keeps its earlier reply while a later notice turn answers in it', () => {
    expect(runResult(notice({ blocks: [main()] }))).toBeUndefined();
    expect(runResult(notice({ status: 'running', blocks: [main('이전 답', 'running')] }))).toBe('이전 답');
    expect(runResult(notice({ status: 'running', blocks: [main(undefined, 'running')] }))).toBeUndefined();
    // A request still running shows none, as before.
    expect(runResult(run({ status: 'running', blocks: [main('중간', 'running')] }))).toBeUndefined();
  });

  it('draws background blocks only inside a notice run', () => {
    const blocks = [
      { id: 'm', kind: 'main', title: '요청 1', status: 'completed' },
      { id: 'a', kind: 'agent', title: '검색', status: 'completed' },
      { id: 'bg', kind: 'agent', title: '배경', status: 'completed', background: true },
      { id: 't', kind: 'task', title: '빌드', status: 'completed', background: true },
      { id: 'steer', kind: 'steer', title: '중간 요청', status: 'completed' },
    ];
    const request = normalizeMighty({ style: 'cli', runs: [{ id: 'r1', input: 'x', status: 'completed', blocks }] })!.runs[0]!;
    expect(request.blocks.map((entry) => entry.id)).toEqual(['m', 'a', 'steer']);
    expect(request.noticeRun).toBeUndefined();
    const copies = normalizeMighty({ style: 'cli', runs: [{ id: 'r1:notice:1', input: 'x', status: 'completed', noticeRun: true, blocks }] })!.runs[0]!;
    expect(copies.blocks.map((entry) => entry.id)).toEqual(['m', 'a', 'bg', 't', 'steer']);
    expect(copies.noticeRun).toBe(true);
    // Only the word true counts.
    expect(normalizeMighty({ style: 'cli', runs: [{ id: 'r2', input: 'x', status: 'completed', noticeRun: 'yes', blocks }] })!.runs[0]!.noticeRun).toBeUndefined();
  });
});
