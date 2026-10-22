import type { SecretStore } from '@/lib/host-secrets';
import {
  createPushSettings,
  DEFAULT_PUSH_SETTINGS,
  enabledKinds,
  parsePushSettings,
  PUSH_SETTINGS_KEY,
} from '@/lib/push/settings';
import { createViewingReporter, VIEWING_REFRESH_MS } from '@/lib/push/viewing';

function memoryStore(): SecretStore & { items: Map<string, string> } {
  const items = new Map<string, string>();
  return {
    items,
    getItemAsync: async (key) => items.get(key) ?? null,
    setItemAsync: async (key, value) => void items.set(key, value),
    deleteItemAsync: async (key) => void items.delete(key),
  };
}

describe('push settings', () => {
  it('turns everything on by default and reads a partial file', () => {
    expect(parsePushSettings(null)).toEqual(DEFAULT_PUSH_SETTINGS);
    expect(enabledKinds(DEFAULT_PUSH_SETTINGS)).toEqual(['completed', 'failed', 'permission', 'question', 'plan', 'signin']);
    const partial = parsePushSettings(JSON.stringify({ kinds: { completed: false }, crashReports: false }));
    expect(partial.kinds.completed).toBe(false);
    expect(partial.kinds.permission).toBe(true);
    expect(partial.crashReports).toBe(false);
    expect(parsePushSettings('{broken')).toEqual(DEFAULT_PUSH_SETTINGS);
  });

  it('keeps a change and tells listeners', async () => {
    const store = memoryStore();
    const settings = createPushSettings(store);
    await settings.ready;
    let told = 0;
    settings.subscribe(() => { told += 1; });
    await settings.set({ ...settings.get(), crashReports: false });
    expect(told).toBe(1);
    expect(JSON.parse(store.items.get(PUSH_SETTINGS_KEY)!).crashReports).toBe(false);
    const again = createPushSettings(store);
    await again.ready;
    expect(again.get().crashReports).toBe(false);
  });

  it('tells listeners when the saved file turns out different, and a change made before it was read wins', async () => {
    const store = memoryStore();
    store.items.set(PUSH_SETTINGS_KEY, JSON.stringify({ kinds: { question: false } }));
    const loading = createPushSettings(store);
    let told = 0;
    loading.subscribe(() => { told += 1; });
    await loading.ready;
    expect(told).toBe(1);
    expect(enabledKinds(loading.get())).not.toContain('question');

    const early = createPushSettings(store);
    await early.set({ ...DEFAULT_PUSH_SETTINGS, crashReports: false });
    await early.ready;
    expect(early.get()).toEqual({ ...DEFAULT_PUSH_SETTINGS, crashReports: false });
  });
});

describe('viewing reporter', () => {
  function harness() {
    const sent: Array<[string, string | null]> = [];
    let timer: (() => void) | null = null;
    const reporter = createViewingReporter({
      send: async (storageId, sessionId) => void sent.push([storageId, sessionId]),
      setInterval: (run, ms) => { expect(ms).toBe(VIEWING_REFRESH_MS); timer = run; return 1; },
      clearInterval: () => { timer = null; },
    });
    return { sent, reporter, tick: () => timer?.(), running: () => timer !== null };
  }

  it('says which session is on screen, repeats it, and clears it on leaving', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.tick();
    h.reporter.leave();
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', 'pane-1'], ['host-a', null]]);
    expect(h.running()).toBe(false);
  });

  it('clears the view while the app is in the background and says it again on return', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.reporter.setActive(false);
    expect(h.running()).toBe(false);
    h.reporter.setActive(true);
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', null], ['host-a', 'pane-1']]);
  });

  it('tells the old host it is no longer viewed when another host opens', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.reporter.enter('host-b', 'pane-9');
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', null], ['host-b', 'pane-9']]);
  });

  it('replaces the session on the same host without clearing it first', () => {
    const h = harness();
    h.reporter.enter('host-a', 'pane-1');
    h.reporter.enter('host-a', 'pane-2');
    h.tick();
    expect(h.sent).toEqual([['host-a', 'pane-1'], ['host-a', 'pane-2'], ['host-a', 'pane-2']]);
  });

  it('says nothing while in the background, even on entering or leaving', () => {
    const h = harness();
    h.reporter.setActive(false);
    h.reporter.enter('host-a', 'pane-1');
    expect(h.running()).toBe(false);
    h.reporter.leave();
    h.reporter.setActive(true);
    expect(h.sent).toEqual([]);
    expect(h.running()).toBe(false);
  });
});
