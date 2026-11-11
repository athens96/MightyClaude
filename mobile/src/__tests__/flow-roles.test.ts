import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createElement } from 'react';
import { StyleSheet, Text } from 'react-native';
import TestRenderer, { act, type ReactTestInstance, type ReactTestRenderer } from 'react-test-renderer';
import type { MobileMighty } from '@/api/types';
import { FlowLogStrip } from '@/components/flow-roles';
import { MightyRunList } from '@/components/mighty-blocks';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';
import {
  advisorShown,
  advisorWhen,
  cardFigures,
  cardTime,
  delegateText,
  identityLine,
  inkColor,
  logClock,
  markWord,
  parseAdvisor,
  parseCard,
  parseDelegate,
  parseFlowLog,
  parseReturning,
  parseRole,
} from '@/lib/flow-roles';
import { resetLanguage } from '@/lib/i18n';
import { normalizeMighty } from '@/lib/mighty';
import { darkPalette, lightPalette } from '@/theme';

/**
 * native/contracts/fixtures/mobile-roles.json: the role view both hosts send for the same saved runs (the Mac's MobileFlowTests,
 * Windows Core's MobileFlowVerification), rendered here as the phone draws it.
 */
const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/mobile-roles.json'), 'utf8'),
) as { cases: { name: string; expect: Record<string, unknown> }[] };
const [claudeCase, codexCase] = fixture.cases;

/** The case's payload as a host sends it: the fixture's keys and a plain style. */
function payload(expect: Record<string, unknown>): Record<string, unknown> {
  return { style: 'cli', ...expect };
}

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

function texts(renderer: ReactTestRenderer): string[] {
  return renderer.root.findAllByType(Text).map((node: ReactTestInstance) =>
    [node.props.children].flat(3).filter((part) => typeof part === 'string' || typeof part === 'number').join(''),
  );
}

/** The whole text of a node, nested texts included. */
function textOf(node: ReactTestInstance): string {
  return node
    .findAllByType(Text)
    .map((text) => [text.props.children].flat(3).filter((part) => typeof part === 'string' || typeof part === 'number').join(''))
    .join('');
}

function byTestID(renderer: ReactTestRenderer, id: string): ReactTestInstance {
  const found = renderer.root.findAll((node) => node.props.testID === id && typeof node.type !== 'string');
  if (found.length === 0) throw new Error(`no node ${id}`);
  return found[0]!;
}

function press(node: ReactTestInstance): void {
  const target = node.findAll((child) => typeof child.props.onPress === 'function')[0] ?? node;
  act(() => target.props.onPress());
}

function header(renderer: ReactTestRenderer, title: string): ReactTestInstance {
  const found = renderer.root.findAll(
    (node) => typeof node.props.onPress === 'function' && node.findAllByType(Text).some((text) => [text.props.children].flat(3).join('') === title),
  );
  return found[0]!;
}

describe('the shared fixture, as the phone reads it', () => {
  const mighty = normalizeMighty(payload(claudeCase!.expect), true)!;

  it('keeps a request’s background blocks when the host fanned them out, even from a host that filters', () => {
    expect(mighty.fanOut).toBe(true);
    expect(mighty.runs.map((run) => run.blocks.map((block) => block.id))).toEqual([
      ['r1:main', 'bg'],
      ['r1:notice:1:main', 'r1:notice:1:bg'],
      ['r2:main', 'scout', 'ci', 'wt', 'deep'],
    ]);
    // Without the fan-out the same host's request draws none of them, as before.
    const { fanOut: _fanOut, ...plain } = payload(claudeCase!.expect);
    expect(normalizeMighty(plain, true)!.runs[2]!.blocks.map((block) => block.id)).toEqual(['r2:main', 'scout', 'deep']);
  });

  it('reads every role-view field the hosts send', () => {
    const [request, notice, running] = mighty.runs;
    expect(request!.blocks[0]!.role).toEqual({ name: 'lead', ink: 'taskText', identity: 'Sonnet 5.5 · 사고 강도 · High', summary: '메인 · 사고 강도 · High' });
    expect(request!.blocks[1]!.card?.copy).toEqual({ runId: 'r1:notice:1', blockId: 'r1:notice:1:bg' });
    expect(notice!.blocks[1]!.card).toBeUndefined();
    expect(running!.blocks.find((block) => block.id === 'scout')!.card).toMatchObject({ nested: 1, before: ['실행 중'], after: ['도구 3', '토큰 12K'] });
    expect(running!.blocks.find((block) => block.id === 'wt')!.role?.marks).toEqual(['background', 'worktree']);
    expect(running!.blocks[0]!.delegate?.parts?.map((part) => part.ink)).toEqual(['agentText', 'ink2']);
    expect(running!.returning).toEqual({ text: '병렬 4/4 실행 중 · 보고를 기다리는 중', waiting: true });
    expect(running!.advisor).toMatchObject({ header: '조언자 · Opus 5.5 · 대기 중', state: 'onCall', calls: 1, totals: '호출 1 · 읽은 토큰 34K' });
    expect(mighty.log?.status.map((part) => part.text).join(' · ')).toBe('Sonnet 5.5 실행 중 · 하위 에이전트 3/3 실행 중 · 작업 1개 실행 중 · 조언자 대기');
    expect(mighty.log?.rows).toHaveLength(5);
  });

  it('reads another agent’s pane: every block says who it is, and nothing fans out', () => {
    const codex = normalizeMighty(payload(codexCase!.expect), true)!;
    expect(codex.fanOut).toBeUndefined();
    expect(codex.log).toBeUndefined();
    expect(codex.runs[0]!.blocks.map((block) => [block.role?.name, block.role?.identity, block.card])).toEqual([
      ['lead', 'Codex · 사고 강도 · Medium', undefined],
      ['subagent', undefined, undefined],
    ]);
  });
});

describe('the flow with the role view', () => {
  const runs = normalizeMighty(payload(claudeCase!.expect), true)!.runs;
  // scout started at 10:04:00Z; the phone's clock reads 42 seconds later.
  const scoutStart = 1_794_305_040_000;
  let renderer: ReactTestRenderer | undefined;
  beforeEach(() => {
    jest.useFakeTimers({ now: scoutStart + 42_000 });
  });
  afterEach(() => {
    act(() => renderer?.unmount());
    renderer = undefined;
    jest.useRealTimers();
  });
  const mount = () => {
    act(() => {
      renderer = TestRenderer.create(createElement(MightyRunList, { runs }));
    });
    return renderer!;
  };

  it('draws the running request’s rows: who each is, the compact rows, the delegate lines and the way back', () => {
    const view = mount();
    const shown = texts(view);
    // The lead's identity under 요청 2, and the line over its fan-out.
    expect(shown).toContain('Sonnet 5.5 · 사고 강도 · High');
    expect(textOf(byTestID(view, 'mighty-block-r2:main'))).toContain('요청 2');
    const delegates = view.root.findAll((node) => node.props.testID === 'mighty-delegate' && typeof node.type !== 'string').map(textOf);
    expect(delegates).toEqual(['↳ 3개 병렬 · 하위 에이전트 2 · 작업 1 · 3/3 실행 중', '↳ 하위 에이전트 1개에 위임 · 1/1 실행 중']);
    // The compact rows: name (+N), type · model or the command, the live line, the figures with the phone's time.
    const scout = byTestID(view, 'mighty-block-scout');
    expect(textOf(scout)).toContain('테스트 살피기 (+1)');
    expect(textOf(byTestID(view, 'mighty-card-identity-scout'))).toBe('Explore · Haiku 5.5');
    expect(textOf(byTestID(view, 'mighty-card-activity-scout'))).toBe('login.test');
    expect(textOf(byTestID(view, 'mighty-card-figures-scout'))).toBe('실행 중 · 42초 · 도구 3 · 토큰 12K');
    expect(textOf(byTestID(view, 'mighty-card-identity-ci'))).toBe('작업 · python3 scripts/release.py ci');
    expect(textOf(byTestID(view, 'mighty-card-activity-ci'))).toBe('build 2/3 ok');
    expect(textOf(byTestID(view, 'mighty-card-identity-wt'))).toBe('general-purpose · Sonnet 5.5 · 백그라운드 · worktree');
    // The way back where the result will come.
    expect(textOf(byTestID(view, 'mighty-return'))).toBe('⤴ 병렬 4/4 실행 중 · 보고를 기다리는 중');
  });

  it('keeps a running row’s time moving on the phone’s clock', () => {
    const view = mount();
    expect(textOf(byTestID(view, 'mighty-card-figures-scout'))).toBe('실행 중 · 42초 · 도구 3 · 토큰 12K');
    act(() => jest.advanceTimersByTime(1_000));
    expect(textOf(byTestID(view, 'mighty-card-figures-scout'))).toBe('실행 중 · 43초 · 도구 3 · 토큰 12K');
  });

  it('wears each role’s ink down the row’s edge, as the desktops do', () => {
    const view = mount();
    const bar = (id: string) => {
      const block = byTestID(view, `mighty-block-${id}`);
      const strip = block.findAll((node) => typeof node.type !== 'string' && StyleSheet.flatten(node.props.style)?.width === 3 && StyleSheet.flatten(node.props.style)?.position === 'absolute')[0]!;
      return StyleSheet.flatten(strip.props.style).backgroundColor as string;
    };
    expect(bar('r2:main')).toBe(lightPalette.blockTask);
    expect(bar('scout')).toBe(lightPalette.blockAgent);
    expect(bar('ci')).toBe(lightPalette.textMuted);
  });

  it('sums the advisor up under its request', () => {
    const view = mount();
    const card = textOf(byTestID(view, 'mighty-advisor'));
    expect(card).toContain('조언자 · Opus 5.5 · 대기 중');
    expect(card).toContain(`메인 · ${logClock(1_794_304_990_000)} · 7초`);
    expect(card).toContain('파일 변경 전 · 조언 전달 · 내용 비표시');
    expect(card).toContain('조언은 암호화되어 Claude에게만 전달됩니다. 내용은 표시되지 않습니다.');
    expect(card).toContain('호출 1 · 읽은 토큰 34K');
  });

  it('opens an ended background row’s copy in its notice run', () => {
    const view = mount();
    // 요청 1 is folded (it is not the newest request); its background row ended and was copied.
    press(header(view, '요청 1'));
    const row = byTestID(view, 'mighty-block-bg');
    expect(textOf(row)).toContain('알림 실행의 전체 블록으로 이동');
    expect(textOf(byTestID(view, 'mighty-card-figures-bg'))).toBe('완료 · 1분 30초 · 도구 4 · 토큰 12K');
    press(row);
    // The notice run opened, and with it the copy.
    const copy = byTestID(view, 'mighty-block-r1:notice:1:bg');
    expect(textOf(copy)).toContain('접기');
    expect(StyleSheet.flatten(copy.props.style).borderColor).toBe(lightPalette.accent);
    // Ringed for the design's highlight time, then open like any block the reader opened.
    act(() => jest.advanceTimersByTime(1_200));
    const settled = byTestID(view, 'mighty-block-r1:notice:1:bg');
    expect(StyleSheet.flatten(settled.props.style).borderColor).not.toBe(lightPalette.accent);
    expect(textOf(settled)).toContain('접기');
  });

  it('opens a row in place when its copy is not in the list', () => {
    const withoutNotice = runs.filter((run) => !run.id.includes(':notice:'));
    act(() => {
      renderer = TestRenderer.create(createElement(MightyRunList, { runs: withoutNotice }));
    });
    press(header(renderer!, '요청 1'));
    const row = byTestID(renderer!, 'mighty-block-bg');
    // It does not offer what it cannot do.
    expect(textOf(row)).not.toContain('알림 실행의 전체 블록으로 이동');
    press(row);
    expect(textOf(byTestID(renderer!, 'mighty-block-bg'))).toContain('접기');
  });
});

describe('the session log strip', () => {
  const log = normalizeMighty(payload(claudeCase!.expect), true)!.log!;
  let renderer: ReactTestRenderer | undefined;
  afterEach(() => {
    act(() => renderer?.unmount());
    renderer = undefined;
  });

  it('is the status line in its roles’ inks, and folds the last rows under it', () => {
    act(() => {
      renderer = TestRenderer.create(createElement(FlowLogStrip, { log }));
    });
    const view = renderer!;
    const status = byTestID(view, 'mighty-log-status');
    expect(textOf(status)).toBe('Sonnet 5.5 실행 중 · 하위 에이전트 3/3 실행 중 · 작업 1개 실행 중 · 조언자 대기');
    const inks = status.findAllByType(Text).slice(1).map((text) => StyleSheet.flatten(text.props.style).color);
    expect(inks).toEqual([lightPalette.blockTask, lightPalette.blockAgent, lightPalette.textMuted, lightPalette.blockSteer]);
    expect(texts(view)).not.toContain(logClock(log.rows[0]!.at));
    press(byTestID(view, 'mighty-log'));
    const shown = texts(view);
    expect(shown).toContain(logClock(log.rows[0]!.at));
    expect(shown.join('')).toContain('조언자 · Opus 5.5  조언 전달 · 내용 비표시');
    expect(shown.join('')).toContain('로그 읽기  시작');
  });
});

describe('reading the role view defensively', () => {
  it('drops what is not a role, a card, a line, a way back, an advisor or a log', () => {
    for (const junk of [undefined, null, 'lead', 7, [], { name: '' }, { name: 42 }]) expect(parseRole(junk)).toBeUndefined();
    expect(parseRole({ name: 'subagent', ink: 'agentText', summary: 's', marks: ['fork', 'teleport', 9, 'background'] })?.marks).toEqual(['fork', 'background']);
    expect(parseCard('x')).toBeUndefined();
    expect(parseCard({ before: 'running', startedAt: -1, endedAt: Number.NaN, nested: 0, copy: { runId: 'r' } })).toEqual({ before: [], after: [] });
    expect(parseDelegate({ text: 'no head' })).toBeUndefined();
    expect(parseDelegate({ head: '하위 에이전트 2개에 위임', progress: '1/2 실행 중' })?.text).toBe('하위 에이전트 2개에 위임 · 1/2 실행 중');
    expect(parseReturning({ text: '', waiting: true })).toBeUndefined();
    expect(parseReturning({ text: '메인으로 복귀 · 검토 중', waiting: 'yes' })).toEqual({ text: '메인으로 복귀 · 검토 중', waiting: false });
    expect(parseAdvisor({ rows: [] })).toBeUndefined();
    const advisor = parseAdvisor({ header: '조언자', rows: [{ id: 'a', seconds: 2.5 }, { caller: 'no id' }], calls: 0, totals: '호출 1' })!;
    expect(advisor.rows).toEqual([{ id: 'a', caller: '', ink: '', line: '', state: '' }]);
    expect(advisor.calls).toBe(1);
    expect(parseFlowLog({ status: [], rows: [] })).toBeUndefined();
    expect(parseFlowLog({ status: [{ text: '실행 중', ink: 'taskText' }], rows: [{ id: 'x', at: 'now', what: 'Read' }] })?.rows).toEqual([]);
  });

  it('maps the desktops’ inks onto the palette, an unknown one onto the neutral ink', () => {
    expect(inkColor(lightPalette, 'taskText')).toBe(lightPalette.blockTask);
    expect(inkColor(lightPalette, 'errText')).toBe(lightPalette.danger);
    expect(inkColor(lightPalette, 'questionText')).toBe(lightPalette.blockQuestion);
    for (const unknown of ['', 'purple', 'constructor', '__proto__']) expect(inkColor(darkPalette, unknown)).toBe(darkPalette.textMuted);
  });

  it('says a row’s time only when the host saw it', () => {
    expect(cardTime({}, 'running', 10_000)).toBeUndefined();
    expect(cardTime({ startedAt: 0 }, 'completed', 10_000)).toBeUndefined();
    expect(cardTime({ startedAt: 5_000, endedAt: 1_000 }, 'error', 10_000)).toBeUndefined();
    expect(cardTime({ startedAt: 0, endedAt: 138_000 }, 'completed', 999_000)).toBe('2분 18초');
    expect(cardFigures({ before: ['완료', '종료 코드 0'], after: [] }, 'completed', 0)).toBe('완료 · 종료 코드 0');
    expect(advisorWhen({ seconds: 75 })).toBe('1분 15초');
    expect(logClock(new Date(2026, 10, 10, 9, 5, 7).getTime())).toBe('09:05:07');
  });

  it('words the marks and the identity, and counts the earlier consultations', () => {
    expect(markWord('worktree')).toBe('worktree');
    expect(markWord('constructor')).toBeUndefined();
    expect(identityLine({ name: 'subagent', ink: 'agentText', summary: '', marks: ['background', 'fork'] })).toBe('백그라운드 · 포크');
    expect(identityLine(undefined)).toBe('');
    // Every command of the role view runs in the background: its line is the command alone.
    expect(identityLine({ name: 'task', ink: 'ink2', identity: '작업 · npm test', summary: '', marks: ['background'] })).toBe('작업 · npm test');
    expect(delegateText({ head: '작업 2개 실행', parts: [], progress: '1/2 실행 중' })).toBe('작업 2개 실행 · 1/2 실행 중');
    const rows = Array.from({ length: 5 }, (_, index) => ({ id: `c${index}`, caller: '메인', ink: 'taskText', line: '', state: 'done' }));
    expect(advisorShown({ header: '', rows, calls: 40, totals: '' })).toEqual({ rows: rows.slice(-3), earlier: 37 });
  });

  it('knows the roles capability, and a payload without the role view reads as before', () => {
    expect(hasCapability(parseCapabilities({ capabilities: ['mighty', 'roles'] }), 'roles')).toBe(true);
    const older: MobileMighty = normalizeMighty({ style: 'cli', runs: [{ id: 'r', input: '', status: 'running', blocks: [{ id: 'r:main', kind: 'main', title: '요청 1', status: 'running' }] }] })!;
    expect(older.fanOut).toBeUndefined();
    expect(older.log).toBeUndefined();
    expect(older.runs[0]!.blocks[0]!.role).toBeUndefined();
  });
});
