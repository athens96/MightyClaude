import type { MobileSettings, SettingOption } from '@/api/types';
import { sanitiseRateLimits, sanitiseStatusLines } from '@/lib/status-line';

/**
 * The session panel docked above the composer: the settings chips and the status line
 * that used to sit at the top of the transcript, where reaching them also paged in older
 * history. Collapsed it is one row that sums up the settings, with the log/blocks switch
 * of a Mighty pane beside it.
 */

/** The per-device choice, in the secure store the app already keeps its hosts in. */
export const PANEL_EXPANDED_KEY = 'mightyclaude.sessionPanel.expanded.v1';

/** The part of `expo-secure-store` the panel uses. */
export interface PanelStore {
  getItemAsync(key: string): Promise<string | null>;
  setItemAsync(key: string, value: string): Promise<void>;
}

/** Anything but a stored "1" — nothing stored, a read that failed — is collapsed. */
export function parseExpanded(raw: string | null | undefined): boolean {
  return raw === '1';
}

export async function readPanelExpanded(store: PanelStore): Promise<boolean> {
  try {
    return parseExpanded(await store.getItemAsync(PANEL_EXPANDED_KEY));
  } catch {
    return false;
  }
}

/**
 * A failed write only means the choice is not remembered; it is never an error. Answers
 * whether the value reached the store.
 */
export async function writePanelExpanded(store: PanelStore, expanded: boolean): Promise<boolean> {
  try {
    await store.setItemAsync(PANEL_EXPANDED_KEY, expanded ? '1' : '0');
    return true;
  } catch {
    // The panel still opens and closes; only the next start forgets it.
    return false;
  }
}

/** The remembered choice, shared by every screen that shows a panel. */
export interface PanelState {
  get(): boolean;
  subscribe(listener: () => void): () => void;
  set(expanded: boolean): void;
  /** Settles once the stored value has been read. */
  ready: Promise<void>;
  /** Settles once every write asked for so far has been tried. */
  flushed(): Promise<void>;
}

/**
 * Reads the store straight away, so the first screen does not open collapsed and then
 * jump. A tap that comes before the read has finished wins over what was stored. Writes
 * go one after another and only when the value differs from what the store holds.
 */
export function createPanelState(store: PanelStore): PanelState {
  let value = false;
  let decided = false;
  /** What the store holds as far as we know; unknown until the read has finished. */
  let stored: boolean | undefined;
  const listeners = new Set<() => void>();
  const emit = () => listeners.forEach((listener) => listener());

  const ready = readPanelExpanded(store).then((read) => {
    stored = read;
    if (decided) return;
    decided = true;
    if (read !== value) {
      value = read;
      emit();
    }
  });
  let chain: Promise<void> = ready;

  return {
    get: () => value,
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    set(next) {
      decided = true;
      if (next !== value) {
        value = next;
        emit();
      }
      chain = chain.then(async () => {
        const target = value;
        if (target === stored) return;
        if (await writePanelExpanded(store, target)) stored = target;
      });
    },
    ready,
    flushed: () => chain,
  };
}

/** The fields the collapsed row names, in this order: `Opus 5.5 · High · Auto`. */
const SUMMARY_FIELDS = ['model', 'effort', 'permissionMode'] as const;

function labelOf(options: readonly SettingOption[] | undefined, id: string): string {
  const match = Array.isArray(options) ? options.find((option) => option.id === id) : undefined;
  return match && typeof match.label === 'string' && match.label.length > 0 ? match.label : id;
}

/**
 * The collapsed row's text: the label the host gave each current value, or the id when
 * it gave none. A field the host leaves empty is skipped; with nothing at all the row
 * falls back to `fallback`.
 */
export function settingsSummary(settings: MobileSettings | undefined, fallback: string): string {
  if (!settings) return fallback;
  const options = settings.options as Partial<MobileSettings['options']> | undefined;
  const lists = {
    model: options?.models,
    effort: options?.efforts,
    permissionMode: options?.permissionModes,
  };
  const parts = SUMMARY_FIELDS.map((field) => {
    const value = settings[field];
    return typeof value === 'string' && value.length > 0 ? labelOf(lists[field], value) : '';
  }).filter((part) => part.length > 0);
  return parts.length > 0 ? parts.join(' · ') : fallback;
}

/** The row's spoken name: the title, then the summary unless it is only the title again. */
export function panelAccessibilityLabel(title: string, summary: string): string {
  return summary === title ? title : `${title}: ${summary}`;
}

/**
 * What the panel has to show. `body`: chips or a status line to open the panel onto.
 * `viewSwitch`: the log/blocks switch of a Mighty pane, which sits in the row itself.
 * With neither there is no panel.
 */
export function panelContent(input: {
  /** How many settings chips the pane shows (`settingFields`). */
  fieldCount: number;
  /** "status": the host sends a status line and rate limits. */
  showStatus: boolean;
  statusLine?: unknown;
  rateLimits?: unknown;
  mighty: boolean;
}): { body: boolean; viewSwitch: boolean } {
  const status =
    input.showStatus &&
    (sanitiseStatusLines(input.statusLine).length > 0 || sanitiseRateLimits(input.rateLimits).length > 0);
  return { body: input.fieldCount > 0 || status, viewSwitch: input.mighty };
}

/** What the drawing of the panel depends on. */
export interface PanelView {
  /** The remembered choice. */
  expanded: boolean;
  keyboardShown: boolean;
  /** A question card is docked above the panel. */
  questionPending: boolean;
  /** Opened by a tap while that question is up; it ends with the question. */
  peeking: boolean;
}

/**
 * What is drawn: the remembered choice, except that the panel stays compact while the
 * keyboard is up, and while a question card is docked above it unless the user opened it
 * for that question. Neither changes the remembered choice, which comes back afterwards.
 */
export function panelShownExpanded(view: PanelView): boolean {
  if (view.keyboardShown) return false;
  return view.questionPending ? view.peeking : view.expanded;
}

/**
 * A tap on the row. With the keyboard up the panel is compact whatever was chosen, so
 * the tap means "show it": the keyboard goes down and the panel opens. Otherwise the tap
 * flips what is shown. While a question is up only the peek flips; the remembered choice
 * is left as it was.
 */
export function toggledPanel(view: PanelView): { expanded: boolean; peeking: boolean; dismissKeyboard: boolean } {
  const shown = panelShownExpanded(view);
  const open = view.keyboardShown ? true : !shown;
  return view.questionPending
    ? { expanded: view.expanded, peeking: open, dismissKeyboard: view.keyboardShown }
    : { expanded: open, peeking: view.peeking, dismissKeyboard: view.keyboardShown };
}
