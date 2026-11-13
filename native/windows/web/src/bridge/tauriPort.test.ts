import { describe, expect, it } from 'vitest';
import { Bridge } from './bridge';
import { HOST_MESSAGE, HOST_POST, tauriPort, type TauriApi } from './tauriPort';

/** A Tauri stand-in: `listen` resolves when the test says so; `invoke` records each post and can be told to fail. */
function fakeTauri() {
  const invoked: unknown[] = [];
  let handler: ((event: { payload: unknown }) => void) | null = null;
  let registered: () => void = () => {};
  let refused: (error: Error) => void = () => {};
  let failing = false;
  const api: TauriApi = {
    invoke: (command, args) => {
      expect(command).toBe(HOST_POST);
      if (failing) return Promise.reject(new Error('ipc down'));
      invoked.push(args.message);
      return Promise.resolve(null);
    },
    listen: (event, listener) => {
      expect(event).toBe(HOST_MESSAGE);
      handler = listener;
      return new Promise((resolve, reject) => {
        registered = () => resolve(() => {});
        refused = reject;
      });
    },
  };
  return {
    api,
    invoked,
    register: () => registered(),
    refuse: (error: Error) => refused(error),
    fail: () => (failing = true),
    recover: () => (failing = false),
    emit: (payload: unknown) => handler?.({ payload }),
  };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('tauriPort', () => {
  it('holds every post until the listener is registered, then sends them in order', async () => {
    const tauri = fakeTauri();
    const port = tauriPort(tauri.api);
    port.postMessage({ kind: 'call', id: 1, method: 'getLocale' });
    port.postMessage({ kind: 'call', id: 2, method: 'ready' });
    await settle();
    expect(tauri.invoked).toEqual([]);
    tauri.register();
    await settle();
    expect(tauri.invoked).toEqual([
      { kind: 'call', id: 1, method: 'getLocale' },
      { kind: 'call', id: 2, method: 'ready' },
    ]);
    port.postMessage({ kind: 'call', id: 3, method: 'shellState' });
    await settle();
    expect(tauri.invoked).toHaveLength(3);
  });

  it('hands the host-message payloads to the page as message events', async () => {
    const tauri = fakeTauri();
    const port = tauriPort(tauri.api);
    const seen: unknown[] = [];
    port.addEventListener('message', (event) => seen.push(event.data));
    tauri.register();
    await settle();
    tauri.emit({ kind: 'snapshot', snapshot: { version: 1 } });
    tauri.emit({ kind: 'result', id: 2, ok: true });
    expect(seen).toEqual([{ kind: 'snapshot', snapshot: { version: 1 } }, { kind: 'result', id: 2, ok: true }]);
  });

  it('carries a Bridge call to its result, the push before it first', async () => {
    const tauri = fakeTauri();
    const bridge = new Bridge(tauriPort(tauri.api));
    const pushes: unknown[] = [];
    bridge.onPush((message) => pushes.push(message.kind));
    const ready = bridge.call('ready');
    tauri.register();
    await settle();
    expect(tauri.invoked).toEqual([{ kind: 'call', id: 1, method: 'ready' }]);
    tauri.emit({ kind: 'snapshot', snapshot: {} });
    tauri.emit({ kind: 'result', id: 1, ok: true });
    await expect(ready).resolves.toBeUndefined();
    expect(pushes).toEqual(['snapshot']);
  });

  it('fails a call it cannot deliver instead of leaving it waiting', async () => {
    const tauri = fakeTauri();
    const bridge = new Bridge(tauriPort(tauri.api));
    tauri.register();
    await settle();
    tauri.fail();
    await expect(bridge.call('getLocale')).rejects.toThrow(/host unreachable: Error: ipc down/);
  });

  it('keeps posting after a listener throws on a failed result', async () => {
    const tauri = fakeTauri();
    const port = tauriPort(tauri.api);
    const seen: unknown[] = [];
    let throwing = true;
    port.addEventListener('message', (event) => {
      seen.push(event.data);
      if (throwing) throw new Error('listener failed');
    });
    tauri.register();
    await settle();
    tauri.fail();
    port.postMessage({ kind: 'call', id: 1, method: 'getLocale' });
    await settle();
    expect(seen).toEqual([{ kind: 'result', id: 1, ok: false, error: 'host unreachable: Error: ipc down' }]);
    throwing = false;
    tauri.recover();
    port.postMessage({ kind: 'call', id: 2, method: 'ready' });
    await settle();
    expect(tauri.invoked).toEqual([{ kind: 'call', id: 2, method: 'ready' }]);
  });

  it('fails every call when the listener cannot be registered', async () => {
    const tauri = fakeTauri();
    const bridge = new Bridge(tauriPort(tauri.api));
    const first = bridge.call('getLocale');
    tauri.refuse(new Error('no event permission'));
    await expect(first).rejects.toThrow(/no event permission/);
    await expect(bridge.call('ready')).rejects.toThrow(/no event permission/);
    expect(tauri.invoked).toEqual([]);
  });
});
