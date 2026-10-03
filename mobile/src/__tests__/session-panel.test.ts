import type { MobileSettings } from '@/api/types';
import {
  PANEL_EXPANDED_KEY,
  createPanelState,
  panelAccessibilityLabel,
  panelContent,
  panelShownExpanded,
  parseExpanded,
  readPanelExpanded,
  settingsSummary,
  toggledPanel,
  writePanelExpanded,
  type PanelStore,
} from '@/lib/session-panel';

function settings(overrides: Partial<MobileSettings> = {}): MobileSettings {
  return {
    editable: true,
    model: 'claude-opus-5-5',
    permissionMode: 'auto',
    effort: 'high',
    agentViewMode: 'cli',
    mightyStyle: 'ouroboros',
    options: {
      models: [{ id: 'claude-opus-5-5', label: 'Opus 5.5' }],
      permissionModes: [{ id: 'auto', label: 'Auto' }],
      efforts: [{ id: 'high', label: 'High' }],
      mightyStyles: [],
    },
    ...overrides,
  };
}

function memoryStore(initial: Record<string, string> = {}): PanelStore & { data: Record<string, string> } {
  const data = { ...initial };
  return {
    data,
    getItemAsync: async (key) => data[key] ?? null,
    setItemAsync: async (key, value) => {
      data[key] = value;
    },
  };
}

describe('session panel summary', () => {
  it('names model, effort and permission mode in that order', () => {
    expect(settingsSummary(settings(), 'Session')).toBe('Opus 5.5 · High · Auto');
  });

  it('falls back to the id when the host gave no label for the value', () => {
    expect(settingsSummary(settings({ model: 'claude-sonnet-9' }), 'Session')).toBe(
      'claude-sonnet-9 · High · Auto',
    );
  });

  it('skips a field the host leaves empty', () => {
    const { effort: _effort, ...withoutEffort } = settings();
    expect(settingsSummary(withoutEffort as MobileSettings, 'Session')).toBe('Opus 5.5 · Auto');
    expect(settingsSummary(settings({ permissionMode: '' }), 'Session')).toBe('Opus 5.5 · High');
  });

  it('copes with option lists the host left out', () => {
    const bare = settings({ options: undefined as unknown as MobileSettings['options'] });
    expect(settingsSummary(bare, 'Session')).toBe('claude-opus-5-5 · high · auto');
  });

  it('uses the fallback with no settings or nothing to name', () => {
    expect(settingsSummary(undefined, 'Session')).toBe('Session');
    expect(settingsSummary(settings({ model: '', permissionMode: '', effort: '' }), 'Session')).toBe('Session');
  });
});

const view = (overrides: Partial<{ expanded: boolean; keyboardShown: boolean; questionPending: boolean; peeking: boolean }> = {}) => ({
  expanded: false,
  keyboardShown: false,
  questionPending: false,
  peeking: false,
  ...overrides,
});

describe('session panel state', () => {
  it('shows the remembered choice when nothing is in the way', () => {
    expect(panelShownExpanded(view({ expanded: true }))).toBe(true);
    expect(panelShownExpanded(view({ expanded: false }))).toBe(false);
  });

  it('stays compact while the keyboard is up, whatever was chosen', () => {
    expect(panelShownExpanded(view({ expanded: true, keyboardShown: true }))).toBe(false);
    expect(panelShownExpanded(view({ expanded: true, keyboardShown: true, questionPending: true, peeking: true }))).toBe(
      false,
    );
  });

  it('stays compact while a question is docked, unless opened for it', () => {
    expect(panelShownExpanded(view({ expanded: true, questionPending: true }))).toBe(false);
    expect(panelShownExpanded(view({ expanded: false, questionPending: true, peeking: true }))).toBe(true);
    // A peek left over from a question that has gone does not open the panel.
    expect(panelShownExpanded(view({ expanded: false, peeking: true }))).toBe(false);
  });

  it('flips on a tap with the keyboard down', () => {
    expect(toggledPanel(view({ expanded: false }))).toEqual({ expanded: true, peeking: false, dismissKeyboard: false });
    expect(toggledPanel(view({ expanded: true }))).toEqual({ expanded: false, peeking: false, dismissKeyboard: false });
  });

  it('opens and lowers the keyboard on a tap with the keyboard up', () => {
    expect(toggledPanel(view({ expanded: false, keyboardShown: true }))).toEqual({
      expanded: true,
      peeking: false,
      dismissKeyboard: true,
    });
    expect(toggledPanel(view({ expanded: true, keyboardShown: true }))).toEqual({
      expanded: true,
      peeking: false,
      dismissKeyboard: true,
    });
  });

  it('only peeks while a question is up, keeping the remembered choice', () => {
    expect(toggledPanel(view({ expanded: true, questionPending: true }))).toEqual({
      expanded: true,
      peeking: true,
      dismissKeyboard: false,
    });
    expect(toggledPanel(view({ expanded: false, questionPending: true, peeking: true }))).toEqual({
      expanded: false,
      peeking: false,
      dismissKeyboard: false,
    });
    expect(toggledPanel(view({ expanded: false, questionPending: true, keyboardShown: true }))).toEqual({
      expanded: false,
      peeking: true,
      dismissKeyboard: true,
    });
  });
});

describe('session panel content', () => {
  const statusLine = { lines: [[{ text: 'main · 3 files' }]] };
  const rateLimits = [{ label: '5h', usedPercent: 40 }];

  it('has nothing to show without chips, status or Mighty', () => {
    expect(panelContent({ fieldCount: 0, showStatus: true, mighty: false })).toEqual({ body: false, viewSwitch: false });
    expect(panelContent({ fieldCount: 0, showStatus: true, statusLine: { lines: [] }, mighty: false })).toEqual({
      body: false,
      viewSwitch: false,
    });
  });

  it('opens onto the chips', () => {
    expect(panelContent({ fieldCount: 3, showStatus: false, mighty: false })).toEqual({ body: true, viewSwitch: false });
  });

  it('opens onto a status line or rate limits only on a host that sends them', () => {
    expect(panelContent({ fieldCount: 0, showStatus: true, statusLine, mighty: false })).toEqual({
      body: true,
      viewSwitch: false,
    });
    expect(panelContent({ fieldCount: 0, showStatus: true, rateLimits, mighty: false })).toEqual({
      body: true,
      viewSwitch: false,
    });
    expect(panelContent({ fieldCount: 0, showStatus: false, statusLine, rateLimits, mighty: false })).toEqual({
      body: false,
      viewSwitch: false,
    });
  });

  it('carries the log/blocks switch for a Mighty pane, with or without a body', () => {
    expect(panelContent({ fieldCount: 0, showStatus: false, mighty: true })).toEqual({ body: false, viewSwitch: true });
    expect(panelContent({ fieldCount: 2, showStatus: false, mighty: true })).toEqual({ body: true, viewSwitch: true });
  });
});

describe('session panel accessibility', () => {
  it('names the summary after the title, but never the title twice', () => {
    expect(panelAccessibilityLabel('Session settings', 'Opus 5.5 · High')).toBe('Session settings: Opus 5.5 · High');
    expect(panelAccessibilityLabel('Session settings', 'Session settings')).toBe('Session settings');
  });
});

describe('session panel storage', () => {
  it('is collapsed unless "1" was stored', () => {
    expect(parseExpanded(null)).toBe(false);
    expect(parseExpanded(undefined)).toBe(false);
    expect(parseExpanded('0')).toBe(false);
    expect(parseExpanded('true')).toBe(false);
    expect(parseExpanded('1')).toBe(true);
  });

  it('round-trips the choice under its key', async () => {
    const store = memoryStore();
    expect(await readPanelExpanded(store)).toBe(false);
    await writePanelExpanded(store, true);
    expect(store.data[PANEL_EXPANDED_KEY]).toBe('1');
    expect(await readPanelExpanded(store)).toBe(true);
    await writePanelExpanded(store, false);
    expect(store.data[PANEL_EXPANDED_KEY]).toBe('0');
    expect(await readPanelExpanded(store)).toBe(false);
  });

  it('uses a key the secure store accepts', () => {
    expect(PANEL_EXPANDED_KEY).toMatch(/^[A-Za-z0-9._-]+$/);
  });

  it('treats a failing store as collapsed and never throws', async () => {
    const broken: PanelStore = {
      getItemAsync: () => Promise.reject(new Error('locked')),
      setItemAsync: () => Promise.reject(new Error('locked')),
    };
    await expect(readPanelExpanded(broken)).resolves.toBe(false);
    await expect(writePanelExpanded(broken, true)).resolves.toBe(false);
  });
});

describe('shared panel state', () => {
  function countingStore(initial: Record<string, string> = {}) {
    const store = memoryStore(initial);
    const writes: string[] = [];
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    return {
      store: {
        getItemAsync: async (key: string) => {
          await gate;
          return store.getItemAsync(key);
        },
        setItemAsync: async (key: string, value: string) => {
          writes.push(value);
          await store.setItemAsync(key, value);
        },
      },
      data: store.data,
      writes,
      release: () => release?.(),
    };
  }

  it('reads the store once and tells every subscriber', async () => {
    const fake = countingStore({ [PANEL_EXPANDED_KEY]: '1' });
    const state = createPanelState(fake.store);
    const heard: boolean[] = [];
    state.subscribe(() => heard.push(state.get()));
    state.subscribe(() => heard.push(state.get()));
    expect(state.get()).toBe(false);
    fake.release();
    await state.ready;
    expect(state.get()).toBe(true);
    expect(heard).toEqual([true, true]);
  });

  it('lets a tap that beats the read win, and writes it once the read is in', async () => {
    const fake = countingStore({ [PANEL_EXPANDED_KEY]: '0' });
    const state = createPanelState(fake.store);
    state.set(true);
    expect(state.get()).toBe(true);
    expect(fake.writes).toEqual([]);
    fake.release();
    await state.flushed();
    expect(state.get()).toBe(true);
    expect(fake.data[PANEL_EXPANDED_KEY]).toBe('1');
    expect(fake.writes).toEqual(['1']);
  });

  it('skips writes that would not change the store and keeps them in order', async () => {
    const fake = countingStore({ [PANEL_EXPANDED_KEY]: '1' });
    const state = createPanelState(fake.store);
    fake.release();
    await state.ready;
    state.set(true);
    await state.flushed();
    expect(fake.writes).toEqual([]);
    state.set(false);
    state.set(true);
    state.set(false);
    await state.flushed();
    expect(fake.writes).toEqual(['0']);
    expect(fake.data[PANEL_EXPANDED_KEY]).toBe('0');
  });

  it('stops telling a listener once it unsubscribes', async () => {
    const fake = countingStore();
    const state = createPanelState(fake.store);
    fake.release();
    await state.ready;
    const heard: boolean[] = [];
    const unsubscribe = state.subscribe(() => heard.push(state.get()));
    state.set(true);
    unsubscribe();
    state.set(false);
    expect(heard).toEqual([true]);
  });
});
