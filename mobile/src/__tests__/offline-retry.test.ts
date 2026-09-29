import { probeHost } from '@/api/client';
import {
  RETRY_MAX_MS,
  RETRY_MIN_MS,
  retryUnreachableHosts,
  unreachableHostIds,
} from '@/lib/offline-retry';
import { useHostsStore, type PairedHost } from '@/store/hosts';

jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(async () => null),
  setItemAsync: jest.fn(async () => undefined),
  deleteItemAsync: jest.fn(async () => undefined),
}));
jest.mock('@/api/client', () => ({
  closeHostClient: jest.fn(),
  describeError: (error: unknown) => String(error),
  peekHostConnection: () => undefined,
  probeHost: jest.fn(),
}));

/** The part of the hosts store the retry reads: who is down, and a change signal. */
function fakeList(...initiallyDown: string[]) {
  const down = new Set(initiallyDown);
  const listeners = new Set<() => void>();
  return {
    unreachable: () => [...down],
    subscribe: (listener: () => void) => {
      listeners.add(listener);
      return () => void listeners.delete(listener);
    },
    set(id: string, isDown: boolean) {
      if (isDown) down.add(id);
      else down.delete(id);
      for (const listener of listeners) listener();
    },
    listeners,
  };
}

describe('unreachableHostIds', () => {
  it('picks the hosts shown offline, not the refused or the unknown', () => {
    const hosts = [{ id: 'a' }, { id: 'b' }, { id: 'c' }, { id: 'd' }, { id: 'e' }];
    const status = {
      a: { reachability: 'offline' as const },
      b: { reachability: 'relay-offline' as const },
      c: { reachability: 'unauthorized' as const },
      d: { reachability: 'online' as const },
    };
    expect(unreachableHostIds(hosts, status)).toEqual(['a', 'b']);
  });
});

describe('retryUnreachableHosts', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('asks again with a growing delay until the host is back', async () => {
    const list = fakeList('mac');
    const refresh = jest.fn(async () => undefined);
    const stop = retryUnreachableHosts({ ...list, refresh });

    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS);
    expect(refresh).toHaveBeenCalledTimes(1);
    // Then 3 s, then 6 s between probes.
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS * 2 - 1);
    expect(refresh).toHaveBeenCalledTimes(1);
    await jest.advanceTimersByTimeAsync(1);
    expect(refresh).toHaveBeenCalledTimes(2);
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS * 4);
    expect(refresh).toHaveBeenCalledTimes(3);

    list.set('mac', false);
    await jest.advanceTimersByTimeAsync(RETRY_MAX_MS * 4);
    expect(refresh).toHaveBeenCalledTimes(3);
    stop();
  });

  it('never waits longer than the cap, and starts short again after a recovery', async () => {
    const list = fakeList('mac');
    const refresh = jest.fn(async () => undefined);
    const stop = retryUnreachableHosts({ ...list, refresh });

    await jest.advanceTimersByTimeAsync(RETRY_MAX_MS * 10);
    const before = refresh.mock.calls.length;
    await jest.advanceTimersByTimeAsync(RETRY_MAX_MS);
    expect(refresh.mock.calls.length).toBe(before + 1);

    list.set('mac', false);
    await jest.advanceTimersByTimeAsync(RETRY_MAX_MS);
    const settled = refresh.mock.calls.length;
    list.set('mac', true);
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS);
    expect(refresh.mock.calls.length).toBe(settled + 1);
    stop();
  });

  it('keeps a backoff per host: a host that just went down is not held to a slower one', async () => {
    const list = fakeList('old');
    const refresh = jest.fn(async (_id: string) => undefined);
    const stop = retryUnreachableHosts({ ...list, refresh });
    const probesOf = (id: string) => refresh.mock.calls.filter(([called]) => called === id).length;

    // `old` has been down a while: probed at 1.5 s, 4.5 s and 10.5 s, next at 22.5 s.
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS * 7);
    expect(probesOf('old')).toBe(3);

    list.set('new', true);
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS);
    expect(probesOf('new')).toBe(1);
    expect(probesOf('old')).toBe(3);
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS * 2);
    expect(probesOf('new')).toBe(2);
    expect(probesOf('old')).toBe(3);
    // `old` keeps its own schedule: next due at 22.5 s.
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS * 7);
    expect(probesOf('old')).toBe(4);
    stop();
  });

  it('runs no timer while every host is reachable', async () => {
    const list = fakeList();
    const refresh = jest.fn(async () => undefined);
    const stop = retryUnreachableHosts({ ...list, refresh });
    expect(jest.getTimerCount()).toBe(0);

    list.set('mac', true);
    expect(jest.getTimerCount()).toBe(1);
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS);
    expect(refresh).toHaveBeenCalledTimes(1);

    list.set('mac', false);
    expect(jest.getTimerCount()).toBe(0);
    stop();
    expect(list.listeners.size).toBe(0);
  });

  it('keeps going when a probe throws, and stops for good when told', async () => {
    const list = fakeList('mac');
    const refresh = jest.fn(async () => {
      throw new Error('unreachable');
    });
    const stop = retryUnreachableHosts({ ...list, refresh });
    await jest.advanceTimersByTimeAsync(RETRY_MIN_MS * 3);
    expect(refresh).toHaveBeenCalledTimes(2);

    stop();
    await jest.advanceTimersByTimeAsync(RETRY_MAX_MS * 4);
    expect(refresh).toHaveBeenCalledTimes(2);
    list.set('other', true);
    expect(jest.getTimerCount()).toBe(0);
  });
});

describe('the list probe that retries in the background', () => {
  const host: PairedHost = {
    id: 'mac',
    name: 'Mac',
    serverId: 'mac',
    relayUrl: 'wss://relay.example',
    hostPublicKeyB64: 'cGs=',
    hostId: 'host',
    appVersion: '1.0.0',
    pairedAt: '2026-09-29T00:00:00.000Z',
  };

  it('leaves an offline row as it is while the probe runs, where a pull shows "checking"', async () => {
    let fail: (error: unknown) => void = () => undefined;
    (probeHost as jest.Mock).mockImplementation(
      () =>
        new Promise((_, reject) => {
          fail = reject;
        }),
    );
    useHostsStore.setState({
      hosts: [host],
      keys: { mac: 'pairing-key' },
      tokens: {},
      status: { mac: { reachability: 'offline', detail: 'down' } },
    });
    const seen: string[] = [];
    const unsubscribe = useHostsStore.subscribe((state) => {
      seen.push(state.status.mac?.reachability ?? 'none');
    });

    const quiet = useHostsStore.getState().refreshReachability('mac', { quiet: true });
    expect(useHostsStore.getState().status.mac?.reachability).toBe('offline');
    fail(new Error('still down'));
    await quiet;
    expect(seen).not.toContain('checking');
    expect(useHostsStore.getState().status.mac?.reachability).toBe('offline');

    const pull = useHostsStore.getState().refreshReachability('mac');
    expect(useHostsStore.getState().status.mac?.reachability).toBe('checking');
    fail(new Error('still down'));
    await pull;
    unsubscribe();
  });
});
