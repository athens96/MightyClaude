import { describe, expect, it } from 'vitest';
import { Bridge, type HostPort } from './bridge';
import type { CallMessage, PushMessage } from './protocol';

function fakePort() {
  const sent: CallMessage[] = [];
  let listener: (event: { data: unknown }) => void = () => {};
  const port: HostPort = {
    postMessage: (message) => void sent.push(message as CallMessage),
    addEventListener: (_type, handler) => { listener = handler; },
  };
  return { port, sent, deliver: (data: unknown) => listener({ data }) };
}

describe('Bridge', () => {
  it('matches results to calls by id', async () => {
    const { port, sent, deliver } = fakePort();
    const bridge = new Bridge(port);
    const first = bridge.call<string>('getLocale');
    const second = bridge.call('selectSession', { id: 's1' });
    expect(sent).toEqual([{ kind: 'call', id: 1, method: 'getLocale' }, { kind: 'call', id: 2, method: 'selectSession', args: { id: 's1' } }]);
    deliver({ kind: 'result', id: 2, ok: false, error: 'no such pane' });
    deliver({ kind: 'result', id: 1, ok: true, value: 'en' });
    await expect(first).resolves.toBe('en');
    await expect(second).rejects.toThrow('no such pane');
  });

  it('hands pushes to the listener and ignores anything else', () => {
    const { port, deliver } = fakePort();
    const bridge = new Bridge(port);
    const pushed: PushMessage[] = [];
    bridge.onPush((message) => pushed.push(message));
    deliver('not json object');
    deliver({ kind: 'result', id: 99, ok: true });
    deliver({ kind: 'delta', sessions: [], removed: [] });
    expect(pushed).toEqual([{ kind: 'delta', sessions: [], removed: [] }]);
  });
});
