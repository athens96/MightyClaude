import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { LogEntry } from '@/api/types';
import { nextPendingChange, parseSubmitResponse, pendingIndicator, pendingVisible, type PendingState } from '@/lib/pending';
import {
  ARRIVED_GRACE,
  STARTED_GRACE,
  applyAnswer,
  beforeSend,
  draftToRestore,
  drawsStandIn,
  failRequest,
  newPendingRequest,
  nextDeadline,
  reconcile,
  sameWords,
  settle,
  shownInBlocks,
  shownInLog,
  shownInStructure,
  type PaneSnapshot,
  type PendingRequest,
} from '@/lib/pending-requests';
import { submitScroll, submitViewFor, type SubmitView } from '@/lib/submit-scroll';
import { motion } from '@/theme';

/**
 * The same files the Mac (PendingTests, DesignTokenParityTests), Windows Core and the Windows page read: the phone's pending
 * rule, submit parser, scroll rule and motion tokens agree with them case by case.
 */
const fixtures = resolve(__dirname, '../../../native/contracts/fixtures');
const pending = JSON.parse(readFileSync(resolve(fixtures, 'pending.json'), 'utf8')) as {
  display: { name: string; startedAt: number; endedAt: number | null; now: number; failed?: boolean; expectVisible: boolean }[];
  submitResponses: { name: string; raw: unknown; parsed: unknown }[];
  scrollTargets: {
    name: string;
    input: { view: SubmitView; own: boolean; chosenBoard: 'newest' | 'older'; nearBottom: boolean };
    expect: { scroll: string; resetBoard: boolean };
  }[];
};
const tokens = JSON.parse(readFileSync(resolve(fixtures, 'design-tokens.json'), 'utf8')) as { motion: Record<string, unknown> };

describe('motion tokens', () => {
  it('are the contract motion, key for key', () => {
    expect(Object.keys(motion).sort()).toEqual(Object.keys(tokens.motion).sort());
    for (const [key, value] of Object.entries(tokens.motion)) expect(motion[key as keyof typeof motion]).toEqual(value);
  });
});

describe('pending display rule', () => {
  it.each(pending.display.map((entry) => [entry.name, entry] as const))('%s', (_name, entry) => {
    expect(pendingVisible(entry.startedAt, entry.endedAt, entry.now, entry.failed ?? false)).toBe(entry.expectVisible);
  });

  it.each(pending.display.map((entry) => [entry.name, entry] as const))('%s, as a state at two origins', (_name, entry) => {
    for (const origin of [0, 1_700_000_000_000]) {
      const since = entry.startedAt + origin;
      const at = entry.endedAt === null ? undefined : entry.endedAt + origin;
      const state: PendingState = entry.failed
        ? { kind: 'failed', message: 'no', since, ...(at !== undefined ? { at } : {}) }
        : at === undefined
          ? { kind: 'pending', since }
          : { kind: 'done', since, at };
      const indicator = pendingIndicator(state, entry.now + origin);
      expect(indicator.kind !== 'hidden').toBe(entry.expectVisible);
      if (entry.failed && at !== undefined && entry.now + origin >= at) expect(indicator).toEqual({ kind: 'failed', message: 'no' });
    }
  });

  it('shows nothing when idle, and a failure with no end at once', () => {
    expect(pendingIndicator({ kind: 'idle' }, 5)).toEqual({ kind: 'hidden' });
    expect(pendingIndicator({ kind: 'failed', message: 'x', since: 0 }, 1)).toEqual({ kind: 'failed', message: 'x' });
  });

  it('wakes only at the moments the indicator may change', () => {
    expect(nextPendingChange({ kind: 'pending', since: 0 }, 0)).toBe(150);
    expect(nextPendingChange({ kind: 'pending', since: 0 }, 150)).toBeNull();
    expect(nextPendingChange({ kind: 'done', since: 0, at: 200 }, 160)).toBe(200);
    expect(nextPendingChange({ kind: 'done', since: 0, at: 200 }, 200)).toBe(451);
    expect(nextPendingChange({ kind: 'idle' }, 0)).toBeNull();
  });
});

describe('submit answers', () => {
  it.each(pending.submitResponses.map((entry) => [entry.name, entry] as const))('%s', (_name, entry) => {
    expect(parseSubmitResponse(entry.raw)).toEqual(entry.parsed);
  });

  it('reads an array or a string as not accepted', () => {
    expect(parseSubmitResponse(['started']).accepted).toBe(false);
    expect(parseSubmitResponse('started').accepted).toBe(false);
  });
});

describe('scroll on submit', () => {
  it.each(pending.scrollTargets.map((entry) => [entry.name, entry] as const))('%s', (_name, entry) => {
    const { view, own, chosenBoard, nearBottom } = entry.input;
    expect(submitScroll(view, own, chosenBoard === 'newest', nearBottom)).toEqual(entry.expect);
  });

  it('names the phone bodies by the shared views', () => {
    expect(submitViewFor('log')).toBe('basic');
    expect(submitViewFor('blocks')).toBe('diagram');
    expect(submitViewFor('structure')).toBe('pipeline');
  });
});

const user = (id: string, text: string, timestamp = '2026-10-27T00:00:00Z'): LogEntry => ({ id, kind: 'user', text, timestamp });
const reply = (id: string): LogEntry => ({ id, kind: 'assistant', text: 'ok', timestamp: '2026-10-27T00:00:00Z' });
const started = (entryId: string | null = null, runId: string | null = null) =>
  ({ accepted: true, outcome: 'started', entryId, runId }) as const;

describe('sent requests on the phone', () => {
  const before: PaneSnapshot = { entries: [user('u1', 'first'), reply('a1')] };

  it('waits for its entry, matched by the answer id', () => {
    let requests: readonly PendingRequest[] = [newPendingRequest(before, 'c1', 'hello', 1000)];
    requests = applyAnswer(requests, 'c1', started('u2'), 1100);
    expect(reconcile(requests, before, 1200)).toBe(requests);
    // Another entry with the same words is not the one the host named.
    expect(reconcile(requests, { entries: [...before.entries, user('x', 'hello')] }, 1200)).toHaveLength(1);
    expect(reconcile(requests, { entries: [...before.entries, user('u2', 'hello')] }, 1200)).toHaveLength(0);
  });

  it('matches by exact words before the answer, never by a prefix', () => {
    const requests = [newPendingRequest(before, 'c1', 'hello world', 1000)];
    expect(reconcile(requests, { entries: [...before.entries, user('u2', 'hello')] }, 1100)).toHaveLength(1);
    expect(reconcile(requests, { entries: [...before.entries, user('u2', ' hello\n world ')] }, 1100)).toHaveLength(0);
    expect(sameWords('a  b', 'a b')).toBe(true);
  });

  it('does not count entries the pane had before the send', () => {
    const requests = [newPendingRequest(before, 'c1', 'first', 1000)];
    expect(reconcile(requests, before, 1100)).toHaveLength(1);
  });

  it('takes any new request entry for a start answered without ids', () => {
    let requests: readonly PendingRequest[] = [newPendingRequest(before, 'c1', 'with a file', 1000)];
    requests = applyAnswer(requests, 'c1', started(), 1100);
    expect(reconcile(requests, { entries: [...before.entries, user('u2', 'with a file\n/tmp/a.png')] }, 1200)).toHaveLength(0);
  });

  it('in the Mighty view waits for the run as well as the entry', () => {
    const mightyBefore: PaneSnapshot = { entries: before.entries, runs: [{ id: 'r1', input: 'first' }] };
    let requests: readonly PendingRequest[] = [newPendingRequest(mightyBefore, 'c1', 'go', 1000)];
    requests = applyAnswer(requests, 'c1', started('u2', 'r2'), 1100);
    const entryOnly: PaneSnapshot = { entries: [...before.entries, user('u2', 'go')], runs: mightyBefore.runs! };
    expect(reconcile(requests, entryOnly, 1200)).toHaveLength(1);
    const both: PaneSnapshot = { entries: entryOnly.entries, runs: [...mightyBefore.runs!, { id: 'r2', input: 'go' }] };
    const result = settle(requests, both, 1200);
    expect(result.requests).toHaveLength(0);
    expect(result.landed).toBe(true);
    // A run with only the words, and not the answer's id, does not count.
    const other: PaneSnapshot = { entries: entryOnly.entries, runs: [...mightyBefore.runs!, { id: 'r9', input: 'go' }] };
    expect(reconcile(requests, other, 1200)).toHaveLength(1);
  });

  it('keeps an arrived request until the 파이프라인 holds its run, at most ARRIVED_GRACE', () => {
    const snapshot: PaneSnapshot = { entries: [...before.entries, user('u2', 'go')], runs: [{ id: 'r2', input: 'go' }] };
    const requests = [newPendingRequest(before, 'c1', 'go', 1000)];
    let held = false;
    const holds = () => held;
    const arrived = reconcile(requests, snapshot, 1100, holds);
    expect(arrived[0]?.phase).toBe('arrived');
    expect(shownInStructure(arrived, new Set(['r1']))).toBe(arrived[0]);
    expect(shownInStructure(arrived, new Set(['r2']))).toBeUndefined();
    expect(shownInLog(arrived, snapshot.entries)).toHaveLength(0);
    expect(nextDeadline(arrived)).toBe(1100 + ARRIVED_GRACE + 1);
    expect(reconcile(arrived, snapshot, 1100 + ARRIVED_GRACE + 1, holds)).toHaveLength(0);
    held = true;
    expect(reconcile(arrived, snapshot, 1200, holds)).toHaveLength(0);
  });

  it('drops a started request whose entry never comes after STARTED_GRACE, and that is no landing', () => {
    const requests = applyAnswer([newPendingRequest(before, 'c1', 'go', 1000)], 'c1', started('u2'), 2000);
    expect(nextDeadline(requests)).toBe(2000 + STARTED_GRACE + 1);
    const result = settle(requests, before, 2000 + STARTED_GRACE + 1);
    expect(result.requests).toHaveLength(0);
    expect(result.landed).toBe(false);
  });

  it('drops a queued or steered request, and keeps a failure until the next send', () => {
    const requests = [newPendingRequest(before, 'c1', 'go', 1000)];
    expect(applyAnswer(requests, 'c1', { accepted: true, outcome: 'queued', entryId: null, runId: null }, 1100)).toHaveLength(0);
    expect(applyAnswer(requests, 'c1', { accepted: true, outcome: 'steered', entryId: null, runId: null }, 1100)).toHaveLength(0);
    const failed = failRequest(requests, 'c1', 'bad model', 1100);
    expect(reconcile(failed, { entries: [...before.entries, user('u2', 'go')] }, 1200)).toBe(failed);
    expect(shownInLog(failed, before.entries)[0]?.message).toBe('bad model');
    expect(shownInBlocks(failed, [])?.phase).toBe('failed');
    expect(beforeSend(failed)).toHaveLength(0);
  });

  it("lets the answer's run win over one matched by words", () => {
    const mighty: PaneSnapshot = { entries: [...before.entries, user('u2', 'go')], runs: [{ id: 'r2', input: 'go' }] };
    const arrived = reconcile([newPendingRequest(before, 'c1', 'go', 1000)], mighty, 1100, () => false);
    expect(arrived[0]?.arrivedRunIds).toEqual(['r2']);
    const answered = applyAnswer(arrived, 'c1', started('u2', 'r3'), 1200);
    expect(answered[0]).toMatchObject({ phase: 'started', runId: 'r3' });
    expect(answered[0]?.arrivedRunIds).toBeUndefined();
  });

  it('draws nothing for a request it only watches', () => {
    const watched = [newPendingRequest(before, 'c1', '/compact', 1000, false)];
    expect(shownInLog(watched, before.entries)).toHaveLength(0);
    expect(shownInBlocks(watched, [])).toBeUndefined();
    expect(settle(watched, { entries: [...before.entries, user('u2', '/compact')] }, 1100).landed).toBe(true);
  });

  it('gives the draft back only to an empty, untouched composer', () => {
    expect(draftToRestore('hello', '', false)).toBe('hello');
    expect(draftToRestore('hello', '', true)).toBeUndefined();
    expect(draftToRestore('hello', 'new', false)).toBeUndefined();
  });

  it('draws a stand-in only for a plain send on an idle pane', () => {
    expect(drawsStandIn('hello', { running: false })).toBe(true);
    expect(drawsStandIn('hello', { running: true })).toBe(false);
    expect(drawsStandIn('hello', { running: false, mode: 'queue' })).toBe(false);
    expect(drawsStandIn(' /compact', { running: false })).toBe(false);
  });
});
