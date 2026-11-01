import type { MobileState } from '@/api/types';
import type { SecretStore } from '@/lib/host-secrets';
import {
  MUTED_PREFIX,
  ROUTE_NAME_MAX_AGE_MS,
  ROUTE_REFRESH_MS,
  ROUTE_SESSION_CAP,
  clipWorkspaceName,
  createWorkspaceMute,
  parseMuted,
  parseRoutes,
  resolvePushWorkspace,
  routesFromState,
  type PushRoutes,
  type RouteFiles,
} from '@/lib/push/workspace-mute';

const NOW = Date.UTC(2026, 10, 1, 12);

function state(workspaces: [string, string][], sessions: [string, string][]): MobileState {
  return {
    protocol: 1,
    revision: 1,
    hostName: 'Mac',
    workspaces: workspaces.map(([id, name]) => ({ id, name, path: `/w/${id}` })),
    sessions: sessions.map(([id, workspaceId]) => ({ id, workspaceId }) as MobileState['sessions'][number]),
  };
}

function memoryStore(): SecretStore & {
  items: Map<string, string>;
  failReads: boolean;
  getItemAsync: jest.Mock;
  setItemAsync: jest.Mock;
} {
  const items = new Map<string, string>();
  const store = {
    items,
    failReads: false,
    getItemAsync: jest.fn(async (key: string) => {
      if (store.failReads) throw new Error('keystore');
      return items.get(key) ?? null;
    }),
    setItemAsync: jest.fn(async (key: string, value: string) => void items.set(key, value)),
    deleteItemAsync: jest.fn(async (key: string) => void items.delete(key)),
  };
  return store;
}

function memoryFiles(): RouteFiles & { files: Map<string, string>; write: jest.Mock; read: jest.Mock } {
  const files = new Map<string, string>();
  return {
    files,
    read: jest.fn(async (id: string) => files.get(id) ?? null),
    write: jest.fn(async (id: string, text: string) => void files.set(id, text)),
    remove: jest.fn(async (id: string) => void files.delete(id)),
  };
}

/** A clock the test moves. */
function clock(start = NOW) {
  let at = start;
  return { now: () => at, advance: (ms: number) => void (at += ms) };
}

const ROUTES: PushRoutes = {
  sessions: { 'pane-1': 'ws-a', 'pane-2': 'ws-b' },
  workspaces: [
    { id: 'ws-a', name: 'demo' },
    { id: 'ws-b', name: 'api' },
    { id: 'ws-c', name: 'twin' },
    { id: 'ws-d', name: 'twin' },
  ],
  learnedAt: NOW,
};

describe('resolvePushWorkspace', () => {
  it('takes the session first, even over a name another workspace has', () => {
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-2', workspace: 'demo' }, NOW)).toBe('ws-b');
  });

  it('falls back to the one workspace with the name', () => {
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: 'demo' }, NOW)).toBe('ws-a');
  });

  it('places nothing on a name two workspaces share, an unknown name, or no routes', () => {
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: 'twin' }, NOW)).toBeNull();
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: 'other' }, NOW)).toBeNull();
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: '' }, NOW)).toBeNull();
    expect(resolvePushWorkspace(null, { sessionId: 'pane-1', workspace: 'demo' }, NOW)).toBeNull();
  });

  it('uses names only while the routes are recent; sessions always', () => {
    const late = NOW + ROUTE_NAME_MAX_AGE_MS;
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: 'demo' }, late)).toBe('ws-a');
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: 'demo' }, late + 1)).toBeNull();
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-1', workspace: 'demo' }, late + 1)).toBe('ws-a');
    // A clock set back before the routes says nothing about their age either.
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'pane-9', workspace: 'demo' }, NOW - 1)).toBeNull();
  });

  it('never reads a session id off the object prototype', () => {
    expect(resolvePushWorkspace(ROUTES, { sessionId: 'toString', workspace: '' }, NOW)).toBeNull();
    expect(resolvePushWorkspace(ROUTES, { sessionId: '__proto__', workspace: '' }, NOW)).toBeNull();
  });
});

describe('routesFromState', () => {
  it('keeps each session’s workspace and the names clipped as the host clips them', () => {
    const long = '가'.repeat(79) + '😀' + 'tail';
    const routes = routesFromState(state([['ws-l', long], ['ws-a', 'demo']], [['pane-2', 'ws-l'], ['pane-1', 'ws-a']]), NOW);
    expect(routes).toEqual({
      sessions: { 'pane-1': 'ws-a', 'pane-2': 'ws-l' },
      workspaces: [
        { id: 'ws-a', name: 'demo' },
        { id: 'ws-l', name: '가'.repeat(79) + '😀' },
      ],
      learnedAt: NOW,
    });
    expect([...clipWorkspaceName(long)]).toHaveLength(80);
    // The push carries the clipped name; it finds the long-named workspace.
    expect(resolvePushWorkspace(routes, { sessionId: 'pane-9', workspace: clipWorkspaceName(long) }, NOW)).toBe('ws-l');
  });

  it('is capped', () => {
    const sessions = Array.from({ length: ROUTE_SESSION_CAP + 5 }, (_, i) => [`p${i}`, 'ws-a'] as [string, string]);
    expect(Object.keys(routesFromState(state([['ws-a', 'demo']], sessions), NOW).sessions)).toHaveLength(ROUTE_SESSION_CAP);
  });

  it('reads back what it wrote and nothing damaged; a file without its time is old', () => {
    const routes = routesFromState(state([['ws-a', 'demo']], [['pane-1', 'ws-a']]), NOW);
    expect(parseRoutes(JSON.stringify(routes))).toEqual(routes);
    expect(parseRoutes(null)).toBeNull();
    expect(parseRoutes('{')).toBeNull();
    expect(parseRoutes('[]')).toBeNull();
    expect(parseRoutes('{"sessions":{"a":1,"b":"ws"},"workspaces":[{"id":"x"},{"id":"y","name":"n"}]}')).toEqual({
      sessions: { b: 'ws' },
      workspaces: [{ id: 'y', name: 'n' }],
      learnedAt: 0,
    });
    expect(parseMuted('["a",1,"a","b"]')).toEqual(['a', 'b']);
    expect(parseMuted('{}')).toEqual([]);
    expect(parseMuted('nope')).toEqual([]);
  });
});

describe('createWorkspaceMute', () => {
  const push = { sessionId: 'pane-1', workspace: 'demo' };

  it('shows everything until a workspace is muted, and keeps the choice per pairing', async () => {
    const store = memoryStore();
    const mute = createWorkspaceMute({ store, files: memoryFiles() });
    await mute.learn('pair-a', state([['ws-a', 'demo']], [['pane-1', 'ws-a']]));
    await mute.learn('pair-b', state([['ws-a', 'demo']], [['pane-1', 'ws-a']]));
    expect(await mute.shouldShow('pair-a', push)).toBe(true);

    expect(await mute.setMuted('pair-a', 'ws-a', true)).toBe(true);
    expect(store.items.get(MUTED_PREFIX + 'pair-a')).toBe('["ws-a"]');
    expect(await mute.shouldShow('pair-a', push)).toBe(false);
    // Another pairing (another host, or the same host paired again under another id) is untouched.
    expect(await mute.shouldShow('pair-b', push)).toBe(true);

    expect(await mute.setMuted('pair-a', 'ws-a', false)).toBe(true);
    expect(store.items.has(MUTED_PREFIX + 'pair-a')).toBe(false);
    expect(await mute.shouldShow('pair-a', push)).toBe(true);
  });

  it('reads the choice and the routes from storage alone (the app may be closed)', async () => {
    const store = memoryStore();
    const files = memoryFiles();
    const first = createWorkspaceMute({ store, files, now: () => NOW });
    await first.learn('pair-a', state([['ws-a', 'demo'], ['ws-b', 'api']], [['pane-1', 'ws-a']]));
    await first.setMuted('pair-a', 'ws-b', true);

    const headless = createWorkspaceMute({ store, files, now: () => NOW + 1_000 });
    expect(await headless.shouldShow('pair-a', push)).toBe(true);
    expect(await headless.shouldShow('pair-a', { sessionId: 'pane-7', workspace: 'api' })).toBe(false);
    expect(headless.mutedNow('pair-a')).toEqual(['ws-b']);
  });

  it('places a push by name only while what the phone saw is recent', async () => {
    const store = memoryStore();
    const files = memoryFiles();
    const time = clock();
    const app = createWorkspaceMute({ store, files, now: time.now });
    await app.learn('pair-a', state([['ws-a', 'demo']], []));
    await app.setMuted('pair-a', 'ws-a', true);
    // A second "demo" may have been added on the host since; its new session is not known here.
    const other = { sessionId: 'pane-new', workspace: 'demo' };

    time.advance(ROUTE_NAME_MAX_AGE_MS);
    expect(await createWorkspaceMute({ store, files, now: time.now }).shouldShow('pair-a', other)).toBe(false);
    time.advance(1);
    expect(await createWorkspaceMute({ store, files, now: time.now }).shouldShow('pair-a', other)).toBe(true);
  });

  it('shows a muted workspace’s push it cannot place', async () => {
    const mute = createWorkspaceMute({ store: memoryStore(), files: memoryFiles() });
    await mute.setMuted('pair-a', 'ws-a', true);
    expect(await mute.shouldShow('pair-a', push)).toBe(true);
    await mute.learn('pair-a', state([['ws-a', 'twin'], ['ws-b', 'twin']], []));
    expect(await mute.shouldShow('pair-a', { sessionId: 'pane-1', workspace: 'twin' })).toBe(true);
  });

  it('replaces the routes with each state, writing only when they change or have aged', async () => {
    const files = memoryFiles();
    const time = clock();
    const mute = createWorkspaceMute({ store: memoryStore(), files, now: time.now });
    await mute.learn('pair-a', state([['ws-a', 'demo'], ['ws-b', 'api']], [['pane-1', 'ws-a']]));
    time.advance(1_000);
    // The same lists in another order are the same routes.
    await mute.learn('pair-a', state([['ws-b', 'api'], ['ws-a', 'demo']], [['pane-1', 'ws-a']]));
    expect(files.write).toHaveBeenCalledTimes(1);

    await mute.setMuted('pair-a', 'ws-a', true);
    expect(await mute.shouldShow('pair-a', push)).toBe(false);
    // The pane moved to ws-b: the next state says so and the old entry is gone.
    await mute.learn('pair-a', state([['ws-a', 'demo'], ['ws-b', 'api']], [['pane-1', 'ws-b']]));
    expect(files.write).toHaveBeenCalledTimes(2);
    expect(await mute.shouldShow('pair-a', push)).toBe(true);
    expect(parseRoutes(files.files.get('pair-a') ?? null)?.sessions).toEqual({ 'pane-1': 'ws-b' });

    // Unchanged routes are written again once they are an hour old, with the new time.
    time.advance(ROUTE_REFRESH_MS);
    await mute.learn('pair-a', state([['ws-a', 'demo'], ['ws-b', 'api']], [['pane-1', 'ws-b']]));
    expect(files.write).toHaveBeenCalledTimes(3);
    expect(parseRoutes(files.files.get('pair-a') ?? null)?.learnedAt).toBe(time.now());
  });

  it('writes again after a failed write', async () => {
    const files = memoryFiles();
    files.write.mockRejectedValueOnce(new Error('disk'));
    const mute = createWorkspaceMute({ store: memoryStore(), files });
    const s = state([['ws-a', 'demo']], [['pane-1', 'ws-a']]);
    await mute.learn('pair-a', s);
    await mute.learn('pair-a', s);
    expect(files.write).toHaveBeenCalledTimes(2);
    expect(files.files.has('pair-a')).toBe(true);
  });

  it('shows everything while the choice cannot be read, and neither saves nor keeps a change then', async () => {
    const store = memoryStore();
    store.items.set(MUTED_PREFIX + 'pair-a', '["ws-a"]');
    store.failReads = true;
    const mute = createWorkspaceMute({ store, files: memoryFiles() });
    await mute.learn('pair-a', state([['ws-a', 'demo']], [['pane-1', 'ws-a']]));
    expect(await mute.shouldShow('pair-a', push)).toBe(true);
    expect(await mute.setMuted('pair-a', 'ws-b', true)).toBe(false);
    expect(store.items.get(MUTED_PREFIX + 'pair-a')).toBe('["ws-a"]');
    expect(mute.mutedNow('pair-a')).toEqual([]);

    store.failReads = false;
    expect(await mute.shouldShow('pair-a', push)).toBe(false);
  });

  it('says a change that could not be written failed, and takes it back', async () => {
    const store = memoryStore();
    store.setItemAsync.mockRejectedValueOnce(new Error('keystore'));
    const mute = createWorkspaceMute({ store, files: memoryFiles() });
    expect(await mute.setMuted('pair-a', 'ws-a', true)).toBe(false);
    expect(mute.mutedNow('pair-a')).toEqual([]);
    expect(await mute.setMuted('pair-a', 'ws-a', true)).toBe(true);
    expect(mute.mutedNow('pair-a')).toEqual(['ws-a']);
  });

  it('never lets a read that began before a change put the old list back', async () => {
    const store = memoryStore();
    let release: () => void = () => undefined;
    store.getItemAsync.mockImplementationOnce(
      (key: string) =>
        new Promise<string | null>((resolve) => {
          release = () => resolve(store.items.get(key) ?? null);
        }),
    );
    const mute = createWorkspaceMute({ store, files: memoryFiles() });
    const heard = jest.fn();
    mute.subscribe(heard);

    // The screen's first read is out; a second ask shares it rather than reading again.
    const mounted = mute.load('pair-a');
    expect(mute.load('pair-a')).toBe(mounted);
    const toggled = mute.setMuted('pair-a', 'ws-a', true);
    release();
    await mounted;
    expect(await toggled).toBe(true);
    expect(store.getItemAsync).toHaveBeenCalledTimes(1);
    expect(mute.mutedNow('pair-a')).toEqual(['ws-a']);

    // A read still out when the pairing is removed does not bring its list back.
    let late: () => void = () => undefined;
    store.getItemAsync.mockImplementationOnce(
      () =>
        new Promise<string | null>((resolve) => {
          late = () => resolve('["ws-x"]');
        }),
    );
    const reading = mute.load('pair-b');
    const removed = mute.forget('pair-b');
    late();
    await removed;
    await reading;
    expect(mute.mutedNow('pair-b')).toEqual([]);
  });

  it('tells listeners and forgets everything of a removed pairing', async () => {
    const store = memoryStore();
    const files = memoryFiles();
    const mute = createWorkspaceMute({ store, files });
    const heard = jest.fn();
    const stop = mute.subscribe(heard);
    await mute.learn('pair-a', state([['ws-a', 'demo']], [['pane-1', 'ws-a']]));
    await mute.learn('pair-b', state([['ws-a', 'demo']], [['pane-1', 'ws-a']]));
    await mute.setMuted('pair-a', 'ws-a', true);
    await mute.setMuted('pair-b', 'ws-a', true);
    expect(heard).toHaveBeenCalled();

    await mute.forget('pair-a');
    expect(store.items.has(MUTED_PREFIX + 'pair-a')).toBe(false);
    expect(files.files.has('pair-a')).toBe(false);
    expect(mute.mutedNow('pair-a')).toEqual([]);
    expect(await mute.shouldShow('pair-a', push)).toBe(true);
    expect(store.items.get(MUTED_PREFIX + 'pair-b')).toBe('["ws-a"]');
    expect(files.files.has('pair-b')).toBe(true);
    stop();
  });

  it('writes nothing back for a removed pairing until it is paired again', async () => {
    const files = memoryFiles();
    const mute = createWorkspaceMute({ store: memoryStore(), files });
    const s = state([['ws-a', 'demo']], [['pane-1', 'ws-a']]);
    // A state learned just before the removal, and one from a poll still in flight after it.
    const before = mute.learn('pair-a', s);
    const removed = mute.forget('pair-a');
    const after = mute.learn('pair-a', state([['ws-b', 'api']], []));
    await Promise.all([before, removed, after]);
    expect(files.files.has('pair-a')).toBe(false);

    mute.resume('pair-a');
    await mute.learn('pair-a', s);
    expect(files.files.has('pair-a')).toBe(true);
  });

  it('runs one pairing’s changes in order', async () => {
    const store = memoryStore();
    const mute = createWorkspaceMute({ store, files: memoryFiles() });
    await Promise.all([
      mute.setMuted('pair-a', 'ws-a', true),
      mute.setMuted('pair-a', 'ws-b', true),
      mute.setMuted('pair-a', 'ws-a', false),
    ]);
    expect(store.items.get(MUTED_PREFIX + 'pair-a')).toBe('["ws-b"]');
  });
});
