import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import type { LogEntry, RunSession } from '../store/models';
import { awaitsFirstOutput } from '../conversation/Conversation';
import { Pending } from './Pending';
import type { PendingState } from './display';
import { pendingBoard } from '../mighty/Structure';
import { StructureNavigation } from '../mighty/structureMotion';
import { submitScroll } from './submitScroll';
import { applyAnswer, newPendingRequest } from './requests';
import { pendingCopies } from '../mighty/Diagram';
import { nodeId, WorkSuffix } from '../mighty/layout';

const services = {
  bridge: { call: () => Promise.resolve(null) },
  store: {},
  t: translator([{ 'graph.state.sending': 'Sending', 'graph.state.running': 'Running' }]),
} as unknown as AppServices;

const render = (props: Parameters<typeof Pending>[0]) => renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services }, createElement(Pending, props)));

describe('<Pending>', () => {
  it('draws nothing for an action that has just begun, so a quick answer never flashes', () => {
    expect(render({ state: { kind: 'pending', since: Date.now() } })).toBe('');
    expect(render({ busy: true })).toBe('');
    expect(render({ busy: false })).toBe('');
  });

  it('draws the bars and 전송 중 once the action has lasted pendingShowAfter', () => {
    const html = render({ state: { kind: 'pending', since: Date.now() - 5000 } });
    expect(html).toContain('data-pending="loading"');
    expect(html).toContain('pending-bars');
    expect(html).toContain('>Sending<');
    expect(html).toContain('aria-label="Sending"');
    expect(render({ state: { kind: 'pending', since: Date.now() - 5000 }, showsLabel: false })).not.toContain('pending-pill');
    expect(render({ state: { kind: 'pending', since: Date.now() - 5000 }, label: 'Working' })).toContain('>Working<');
  });

  it('shows a failure at once in place, unless the caller shows it itself', () => {
    const failed: PendingState = { kind: 'failed', message: 'The CLI is missing', since: Date.now() - 10, at: Date.now() - 5 };
    const html = render({ state: failed });
    expect(html).toContain('role="alert"');
    expect(html).toContain('The CLI is missing');
    expect(render({ state: failed, showsFailure: false })).toBe('');
  });

  it('draws nothing while idle', () => {
    expect(render({ state: { kind: 'idle' } })).toBe('');
  });
});

describe('the running row', () => {
  const entry = (kind: LogEntry['kind']): LogEntry => ({ id: kind, kind, text: kind, timestamp: '2026-10-27T10:00:00Z' });
  const pane = (status: RunSession['status'], logs: LogEntry[]) => ({ status, logs }) as RunSession;

  it('shows from the run start until its first output', () => {
    expect(awaitsFirstOutput(pane('running', [entry('assistant'), entry('user')]))).toBe(true);
    expect(awaitsFirstOutput(pane('running', [entry('user'), entry('assistant')]))).toBe(false);
    expect(awaitsFirstOutput(pane('running', [entry('user'), entry('system')]))).toBe(false);
    expect(awaitsFirstOutput(pane('idle', [entry('user')]))).toBe(false);
    expect(awaitsFirstOutput(pane('running', []))).toBe(false);
  });
});

describe('the pipeline while a request is on its way', () => {
  const session = { id: 'p', logs: [], graphRuns: [] } as unknown as RunSession;
  const request = newPendingRequest(session, 'c9', 'build the page', 1000);
  const t = translator([{ 'graph.structure.column.request': 'Request', 'graph.structure.step.done': 'Done' }]);

  it('draws the request as the newest board: its words as the request card, run and result still to come', () => {
    const board = pendingBoard(request, 3, t);
    expect(board).toMatchObject({ id: 'pending-c9', kind: 'request', ordinal: 3, status: 'starting', runIds: ['pending-c9'] });
    expect(board.columns.map((column) => column.step)).toEqual(['done', 'pending', 'pending']);
    expect(board.columns[0]).toMatchObject({ title: 'Request', stepWord: 'Done', count: 1 });
    expect(board.columns[0]!.cards[0]).toMatchObject({ kind: 'request', text: 'build the page', runId: 'pending-c9' });
    const failed = applyAnswer([request], 'c9', 'blocked', {}, 'why', 2000)[0]!;
    expect(pendingBoard(failed, 3, t)).toMatchObject({ status: 'error', tone: 'err' });
  });

  it('goes back to the newest board from an older one for an own send', () => {
    const older = { id: 'b1', runIds: ['r1'] }, newest = { id: 'b2', runIds: ['r2'] };
    const navigation = new StructureNavigation();
    navigation.select('b1', [older, newest]);
    const boards = [older, newest, pendingBoard(request, 3, t)];
    expect(navigation.onNewest(boards)).toBe(false);
    const decision = submitScroll('pipeline', true, navigation.onNewest(boards), false);
    expect(decision).toEqual({ scroll: 'toRequest', resetBoard: true });
    navigation.showNewest(boards);
    expect(navigation.index(boards)).toBe(2);
  });
});

describe("the diagram's copy for a sent request", () => {
  const session = { id: 'p', logs: [], graphRuns: [] } as unknown as RunSession;
  const request = newPendingRequest(session, 'c7', 'words', 1000);
  const t = translator([]);

  it('covers the stand-in run whole, and on the host run only its request block', () => {
    const stand = pendingCopies(request, 'pending-c7', true, 'claude', t);
    expect([...stand.keys()]).toEqual([nodeId('pending-c7', 'request'), nodeId('pending-c7', WorkSuffix)]);
    const host = pendingCopies(request, 'r1', false, 'claude', t);
    // No 요청 실패 on a run that finished before its own copy came.
    expect([...host.keys()]).toEqual([nodeId('r1', 'request')]);
    const failed = applyAnswer([request], 'c7', 'blocked', {}, 'why', 2000)[0]!;
    expect(pendingCopies(failed, 'pending-c7', true, 'claude', t).get(nodeId('pending-c7', 'result'))).toMatchObject({ kind: 'result', tone: 'err' });
  });
});

