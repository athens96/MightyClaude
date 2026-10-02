import type { MobileSessionSummary } from '@/api/types';
import { resetLanguage } from '@/lib/i18n';
import { rowMeta, rowNote, waitLabel } from '@/lib/session-row';

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

function session(overrides: Partial<MobileSessionSummary>): MobileSessionSummary {
  return {
    id: 's1',
    workspaceId: 'w1',
    title: '세션',
    kind: 'claude',
    provider: 'claude',
    model: 'opus',
    status: 'idle',
    revision: 1,
    updatedAt: '2026-03-01T12:00:00.000Z',
    pendingPermissions: 0,
    pendingQuestions: 0,
    queued: 0,
    terminal: false,
    ...overrides,
  };
}

const glance = { elapsedSeconds: 134, contextPercent: 41.2 };
const line = ({ lead, rest }: { lead: string[]; rest: string[] }) => [...lead, ...rest].join(' · ');

describe('rowMeta', () => {
  it('shows elapsed time and context while a pane runs', () => {
    expect(line(rowMeta(session({ status: 'running' }), glance, undefined))).toBe(
      'Claude · opus · 2:14 · 컨텍스트 41%',
    );
  });

  it('shows them while a pane waits on the user, whatever its own status', () => {
    const asking = session({ status: 'idle', pendingQuestions: 1, model: 'sonnet' });
    expect(line(rowMeta(asking, glance, '방금'))).toBe('Claude · sonnet · 2:14 · 컨텍스트 41% · 방금');
  });

  it.each(['completed', 'stopped', 'error', 'idle'] as const)(
    'leaves them out of a %s pane at rest, which says only how long ago it settled',
    (status) => {
      const meta = rowMeta(session({ status, provider: 'codex', model: 'gpt-5' }), glance, '11분 전');
      expect(meta).toEqual({ lead: ['Codex'], beta: true, rest: ['gpt-5', '11분 전'] });
    },
  );

  it('names a shell by its kind and says it is a local terminal', () => {
    const shell = session({ kind: 'shell', model: '', terminal: true });
    expect(rowMeta(shell, undefined, undefined)).toEqual({ lead: ['셸'], beta: false, rest: ['로컬 터미널'] });
  });

  it('never makes up a figure the host did not send', () => {
    expect(line(rowMeta(session({ status: 'running' }), undefined, undefined))).toBe('Claude · opus');
  });
});

describe('rowNote', () => {
  it("shows a running pane's last step", () => {
    expect(rowNote(session({ status: 'running', preview: { kind: 'tool', text: 'Bash · npx jest' } }))).toEqual({
      kind: 'step',
      text: 'Bash · npx jest',
    });
  });

  it('keeps the reason a pane stopped on an error', () => {
    expect(rowNote(session({ status: 'error', preview: { kind: 'error', text: ' Bash 실패 ' } }))).toEqual({
      kind: 'reason',
      text: 'Bash 실패',
    });
  });

  it('leaves the line out for a pane at rest, an error waiting on the user, or an empty preview', () => {
    expect(rowNote(session({ status: 'completed', preview: { kind: 'text', text: '끝' } }))).toBeUndefined();
    expect(
      rowNote(session({ status: 'error', pendingQuestions: 1, preview: { kind: 'error', text: 'x' } })),
    ).toBeUndefined();
    expect(rowNote(session({ status: 'error', preview: { kind: 'error', text: '  ' } }))).toBeUndefined();
    expect(rowNote(session({ status: 'error' }))).toBeUndefined();
  });
});

describe('waitLabel', () => {
  it('names questions first, then permission requests, and nothing otherwise', () => {
    expect(waitLabel(session({ pendingQuestions: 1, pendingPermissions: 2 }))).toBe('질문 1');
    expect(waitLabel(session({ pendingPermissions: 2 }))).toBe('권한 2');
    expect(waitLabel(session({ status: 'running' }))).toBeUndefined();
  });
});
