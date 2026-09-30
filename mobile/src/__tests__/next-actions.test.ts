import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  fillDraft,
  latestNextActions,
  nextActionText,
  parseNextActions,
  type NextAction,
} from '@/lib/next-actions';

/**
 * The same file `NextActionsTests.swift` reads: both clients must turn every breadcrumb
 * into the same buttons, so a mismatch here means the Mac and the phone disagree.
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/next-actions.json');
const cases = JSON.parse(readFileSync(fixturePath, 'utf8')) as { line: string; options: NextAction[] }[];

describe('next actions', () => {
  it('has a fixture covering every rule', () => {
    expect(cases.length).toBeGreaterThanOrEqual(20);
    expect(cases.some((entry) => entry.options.length === 0)).toBe(true);
    expect(cases.some((entry) => entry.options.length === 4)).toBe(true);
  });

  it.each(cases.map((entry) => [entry.line, entry.options] as const))('parses %j', (line, options) => {
    expect(parseNextActions(line)).toEqual(options);
  });

  it('offers the last reply only while no user message follows it', () => {
    const reply = { id: 'a1', kind: 'assistant', text: '끝\n◆ 완료 → next: `ooo run` 또는 다듬기' };
    const tool = { id: 's1', kind: 'system', text: 'Read' };
    expect(latestNextActions([reply, tool])).toEqual({
      entryId: 'a1',
      actions: [
        { label: '`ooo run`', fill: 'ooo run' },
        { label: '다듬기', fill: '다듬기' },
      ],
    });
    expect(latestNextActions([reply, { id: 'u1', kind: 'user', text: 'ooo run' }])).toBeUndefined();
    expect(latestNextActions([reply, { id: 'a2', kind: 'assistant', text: '다른 답' }])).toBeUndefined();
    expect(latestNextActions([])).toBeUndefined();
  });

  it('shows labels without backticks, keeping a label that would be blank', () => {
    expect(nextActionText({ label: '완료 후 `ooo run`', fill: 'ooo run' })).toBe('완료 후 ooo run');
    expect(nextActionText({ label: '``', fill: '``' })).toBe('``');
  });

  it('never loses the draft when filling', () => {
    expect(fillDraft('', 'ooo run')).toBe('ooo run');
    expect(fillDraft(' \n\u3000', 'ooo run')).toBe('ooo run');
    expect(fillDraft('먼저 확인', 'ooo run')).toBe('먼저 확인\nooo run');
    expect(fillDraft('먼저 확인\n', 'ooo run')).toBe('먼저 확인\nooo run');
    expect(fillDraft('먼저 확인\r\n', 'ooo run')).toBe('먼저 확인\r\nooo run');
    expect(fillDraft('먼저 확인\n\n', 'ooo run')).toBe('먼저 확인\n\nooo run');
    expect(fillDraft('먼저 확인 ', 'ooo run')).toBe('먼저 확인 \nooo run');
  });
});
