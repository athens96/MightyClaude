import { describe, expect, it } from 'vitest';
import fixture from '@tokens/pending.json';
import { Motion } from '../theme/tokens';
import { idle, nextPendingChange, parseSubmitResponse, pendingIndicator, pendingVisible, type PendingState } from './display';

interface DisplayCase { name: string; startedAt: number; endedAt: number | null; now: number; failed?: boolean; expectVisible: boolean }
interface ResponseCase { name: string; raw: unknown; parsed: { accepted: boolean; entryId: string | null; runId: string | null; outcome: string | null } }

const display = fixture.display as DisplayCase[];
const responses = fixture.submitResponses as ResponseCase[];

describe('pending display (pending.json displayRule)', () => {
  it('has cases to read', () => {
    expect(display.length).toBeGreaterThan(20);
    expect(responses.length).toBeGreaterThan(10);
  });

  for (const entry of display) {
    it(entry.name, () => {
      expect(pendingVisible(entry.startedAt, entry.endedAt, entry.now, entry.failed === true)).toBe(entry.expectVisible);
    });
  }

  it('gives the same answers as states, at two origins, with the indicator each shows', () => {
    for (const origin of [0, 1_700_000_000_000]) {
      for (const entry of display) {
        const since = origin + entry.startedAt;
        const now = origin + entry.now;
        const ended = entry.endedAt === null ? null : origin + entry.endedAt;
        const state: PendingState = entry.failed
          ? { kind: 'failed', message: 'nope', since, at: ended ?? undefined }
          : ended === null ? { kind: 'pending', since } : { kind: 'done', since, at: ended };
        const shown = pendingIndicator(state, now);
        expect(shown.kind !== 'hidden', entry.name).toBe(entry.expectVisible);
        if (entry.failed && ended !== null && now >= ended) expect(shown, entry.name).toEqual({ kind: 'failed', message: 'nope' });
        else if (entry.expectVisible) expect(shown, entry.name).toEqual({ kind: 'loading' });
      }
    }
  });

  it('shows nothing while idle, and a failure without an end as failing now', () => {
    expect(pendingIndicator(idle, 5000)).toEqual({ kind: 'hidden' });
    expect(pendingIndicator({ kind: 'failed', message: 'gone', since: 0 }, 10)).toEqual({ kind: 'failed', message: 'gone' });
  });

  it('uses the motion tokens', () => {
    expect(pendingVisible(0, null, Motion.pendingShowAfter - 1)).toBe(false);
    expect(pendingVisible(0, null, Motion.pendingShowAfter)).toBe(true);
    expect(pendingVisible(0, Motion.pendingShowAfter + 1, Motion.pendingShowAfter + Motion.pendingMinVisible)).toBe(true);
    expect(pendingVisible(0, Motion.pendingShowAfter + 1, Motion.pendingShowAfter + Motion.pendingMinVisible + 1)).toBe(false);
    // Custom numbers are taken as given.
    expect(pendingVisible(0, null, 10, false, 10, 20)).toBe(true);
  });

  it('wakes only at the moments the indicator may change', () => {
    expect(nextPendingChange(idle, 0)).toBeNull();
    expect(nextPendingChange({ kind: 'pending', since: 1000 }, 1000)).toBe(1150);
    expect(nextPendingChange({ kind: 'pending', since: 1000 }, 1150)).toBeNull();
    // Ended at 200: shown since 150, kept until 450 inclusive, so drawn again at 451.
    expect(nextPendingChange({ kind: 'done', since: 0, at: 200 }, 160)).toBe(200);
    expect(nextPendingChange({ kind: 'done', since: 0, at: 200 }, 200)).toBe(451);
    expect(pendingIndicator({ kind: 'done', since: 0, at: 200 }, 451)).toEqual({ kind: 'hidden' });
    expect(nextPendingChange({ kind: 'failed', message: 'x', since: 0, at: 400 }, 200)).toBe(400);
    expect(nextPendingChange({ kind: 'failed', message: 'x', since: 0, at: 400 }, 400)).toBeNull();
  });
});

describe('submit answers (pending.json submitRule)', () => {
  for (const entry of responses) {
    it(entry.name, () => {
      expect(parseSubmitResponse(entry.raw)).toEqual(entry.parsed);
    });
  }

  it('reads the send bridge words that are not outcomes as not accepted unless accepted is true', () => {
    expect(parseSubmitResponse({ accepted: true, outcome: 'worktree', draft: '' })).toEqual({ accepted: true, entryId: null, runId: null, outcome: null });
    expect(parseSubmitResponse({ accepted: false, outcome: 'notStarted' })).toEqual({ accepted: false, entryId: null, runId: null, outcome: null });
    expect(parseSubmitResponse({ accepted: 1, outcome: 'none' })).toEqual({ accepted: false, entryId: null, runId: null, outcome: null });
    expect(parseSubmitResponse('started')).toEqual({ accepted: false, entryId: null, runId: null, outcome: null });
    expect(parseSubmitResponse([{ outcome: 'started' }])).toEqual({ accepted: false, entryId: null, runId: null, outcome: null });
  });
});
