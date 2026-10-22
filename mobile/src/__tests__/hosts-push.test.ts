import { PUSH_HANDLE_PREFIX } from '@/lib/push/registry';
import { useHostsStore, type PairedHost } from '@/store/hosts';

const mockItems = new Map<string, string>();
jest.mock('expo-secure-store', () => ({
  getItemAsync: jest.fn(async (key: string) => mockItems.get(key) ?? null),
  setItemAsync: jest.fn(async (key: string, value: string) => void mockItems.set(key, value)),
  deleteItemAsync: jest.fn(async (key: string) => void mockItems.delete(key)),
}));

const HANDLE = 'h'.repeat(43);
const SECRET = 's'.repeat(43);

const host: PairedHost = {
  id: 'host-a',
  name: 'Mac',
  serverId: 'a'.repeat(64),
  relayUrl: 'wss://relay.example.invalid',
  hostPublicKeyB64: 'AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8',
  hostId: 'host-id',
  appVersion: '1',
  pairedAt: '2026-10-23T00:00:00.000Z',
};

describe('removing a pairing', () => {
  const realFetch = globalThis.fetch;
  afterEach(() => {
    globalThis.fetch = realFetch;
  });

  it('removes the push record on the phone without waiting for the relay, and deletes the handle there', async () => {
    mockItems.set(
      `${PUSH_HANDLE_PREFIX}host-a`,
      JSON.stringify({ handle: HANDLE, secret: SECRET, relayUrl: host.relayUrl, fcmToken: 't' }),
    );
    let answer: (value: Response) => void = () => undefined;
    const fetchMock = jest.fn(
      (_url: string, _init?: RequestInit) =>
        new Promise<Response>((resolve) => {
          answer = resolve;
        }),
    );
    globalThis.fetch = fetchMock as unknown as typeof fetch;
    useHostsStore.setState({ hosts: [host] });

    await useHostsStore.getState().removeHost('host-a');
    await new Promise((resolve) => setImmediate(resolve));

    expect(useHostsStore.getState().hosts).toEqual([]);
    expect(mockItems.has(`${PUSH_HANDLE_PREFIX}host-a`)).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe(`https://relay.example.invalid/push/v1/handles/${HANDLE}`);
    expect(init?.method).toBe('DELETE');
    expect((init?.headers as Record<string, string>)['authorization']).toBe(`Bearer ${SECRET}`);
    answer({ status: 204 } as Response);
  });
});
