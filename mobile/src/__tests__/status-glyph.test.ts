import type { MobileSessionSummary } from '@/api/types';
import { displayStatus } from '@/lib/dashboard';
import { resetLanguage } from '@/lib/i18n';
import { statusGlyphFor, statusWord } from '@/lib/status-glyph';

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

describe('statusGlyphFor', () => {
  it.each([
    ['running', 'spark', 'run'],
    ['waiting', 'question', 'wait'],
    ['completed', 'check', 'done'],
    ['stopped', 'slashedRing', 'stop'],
    ['error', 'alert', 'err'],
  ] as const)('draws %s as a %s in the %s tone', (status, shape, tone) => {
    expect(statusGlyphFor(status, 'claude')).toEqual({ shape, tone });
  });

  it('gives an idle pane its own glyph: a terminal, a globe, or a quiet ring', () => {
    expect(statusGlyphFor('idle', 'shell').shape).toBe('terminal');
    expect(statusGlyphFor('idle', 'agent-terminal').shape).toBe('terminal');
    expect(statusGlyphFor('idle', 'browser').shape).toBe('globe');
    expect(statusGlyphFor('idle', 'agent-browser').shape).toBe('globe');
    expect(statusGlyphFor('idle', 'claude').shape).toBe('ring');
    expect(statusGlyphFor('idle').shape).toBe('ring');
  });

  it('keeps a state glyph for a shell that is not idle', () => {
    expect(statusGlyphFor('running', 'shell').shape).toBe('spark');
    expect(statusGlyphFor('error', 'shell').shape).toBe('alert');
  });

  it('draws a status word the contract does not list as idle, never a state it did not earn', () => {
    expect(statusGlyphFor('paused', 'claude')).toEqual({ shape: 'ring', tone: 'idle' });
    expect(statusGlyphFor('constructor', 'shell')).toEqual({ shape: 'terminal', tone: 'idle' });
    expect(statusGlyphFor('', 'claude')).toEqual({ shape: 'ring', tone: 'idle' });
  });

  it('shows the question disc for a pane holding a request, whatever its own status', () => {
    const asking = session({ status: 'running', pendingQuestions: 1 });
    expect(statusGlyphFor(displayStatus(asking), asking.kind).shape).toBe('question');
    const permitting = session({ status: 'idle', pendingPermissions: 2 });
    expect(statusGlyphFor(displayStatus(permitting), permitting.kind).shape).toBe('question');
  });
});

describe('statusWord', () => {
  it('labels each glyph with the existing status words', () => {
    expect(statusWord('running')).toBe('실행 중');
    expect(statusWord('waiting')).toBe('응답 대기');
    expect(statusWord('completed')).toBe('완료');
    expect(statusWord('error')).toBe('오류');
    expect(statusWord('stopped')).toBe('중지됨');
    expect(statusWord('idle')).toBe('준비');
  });

  it('shows an unknown word as the host sent it, and an empty one as idle', () => {
    expect(statusWord('paused')).toBe('paused');
    expect(statusWord('constructor')).toBe('constructor');
    expect(statusWord('')).toBe('준비');
  });

  it('follows the English catalog too', () => {
    resetLanguage('en');
    try {
      expect(statusWord('running')).toBe('Running');
      expect(statusWord('waiting')).toBe('Needs you');
    } finally {
      resetLanguage('ko');
    }
  });
});
