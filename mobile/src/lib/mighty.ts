import {
  MAX_BLOCK_ACTIVITY,
  MAX_BLOCK_OUTPUT,
  MAX_BLOCK_SUMMARY,
  MAX_RUN_RESULT,
  type MobileBlock,
  type MobileMighty,
  type MobileMightyRun,
} from '@/api/types';
import {
  blockText,
  inlineText,
  normalizeLegacyOuroboros,
  normalizeLegacyPaperthin,
  normalizeStylePanel,
} from '@/lib/styles';

/**
 * The block list the phone shows instead of the Mac's graph. Everything here is pure:
 * the host's payload is read defensively (a field it adds later, or one it leaves out,
 * must never crash a screen) and turned into labels the list can draw. What a guided
 * style puts around that list lives in `styles.ts`.
 */

/** The Mac's own names for the six block kinds it draws. */
export const BLOCK_KIND_LABELS: Record<string, string> = {
  main: '요청',
  agent: '하위 에이전트',
  task: '백그라운드 작업',
  steer: '중간 요청',
  compact: '컨텍스트 정리',
  question: '질문',
};

/** Marks drawn in place of the Mac's SF Symbols; this app has no vector renderer. */
export const BLOCK_KIND_MARKS: Record<string, string> = {
  main: '●',
  agent: '◆',
  task: '▣',
  steer: '↳',
  compact: '⤡',
  question: '?',
};

/** Neutral mark for a kind the contract does not list. */
export const UNKNOWN_BLOCK_MARK = '○';

/** Longest run input preview kept; the full text is in the transcript. */
export const MAX_INPUT_PREVIEW = 120;
/** Most runs kept from one payload; the contract says the host sends 20. */
export const MAX_RUNS = 20;
/**
 * Most blocks kept per run. A long guided run can carry hundreds, and every one of them
 * is a mounted row; the newest are the ones worth drawing, and the count of the rest is
 * shown instead so nothing disappears silently.
 */
export const MAX_BLOCKS_PER_RUN = 200;

/**
 * Contract 1.11's banned set, collapsed to a space so a preview stays one line. Written
 * with `\u` escapes: the characters themselves would be invisible in this file.
 */
const UNSAFE_PREVIEW =
  /[\u0000-\u001f\u007f-\u009f\u00ad\u061c\u200b-\u200f\u2028\u2029\u202a-\u202e\u2060\u2066-\u2069\ufeff]/g;

/**
 * Both tables are keyed by a word the host chose, so the lookup goes through `Object.hasOwn`:
 * a plain object answers `constructor` with a function and `__proto__` with an object,
 * and either one would reach `<Text>` as something that is not a string.
 */
function labelled(table: Record<string, string>, key: string): string | undefined {
  return Object.hasOwn(table, key) ? table[key] : undefined;
}

/** The label for a block kind, or the word the host sent when we do not know it. */
export function blockKindLabel(kind: string): string {
  return labelled(BLOCK_KIND_LABELS, kind) ?? (kind.length > 0 ? kind : '블록');
}

/** The mark for a block kind; an unknown kind gets the neutral one. */
export function blockKindMark(kind: string): string {
  return labelled(BLOCK_KIND_MARKS, kind) ?? UNKNOWN_BLOCK_MARK;
}

/** The block's own title, falling back to what its kind is called. */
export function blockTitle(block: MobileBlock): string {
  const title = block.title.trim();
  return title.length > 0 ? title : blockKindLabel(block.kind);
}

/** `<제목> · 요청 3`, or just `요청 3` when the run has no title. */
export function runHeading(run: MobileMightyRun, index: number): string {
  const title = run.title?.trim() ?? '';
  return `${title.length > 0 ? `${title} · ` : ''}요청 ${index + 1}`;
}

/** One line of the request, for the collapsed group. */
export function runPreview(input: string): string {
  const flat = input.replace(UNSAFE_PREVIEW, ' ').replace(/\s+/g, ' ').trim();
  if (flat.length <= MAX_INPUT_PREVIEW) return flat;
  return `${flat.slice(0, MAX_INPUT_PREVIEW)}…`;
}

/** Whether the block is still in motion, so what it is doing now outranks its result. */
function inMotion(block: MobileBlock): boolean {
  return block.status === 'running' || block.status === 'waiting';
}

/**
 * What the block was asked, in full. The request block reads the run's own input, which
 * is longer than any summary; a Mac that sends no summary still has that input.
 */
export function blockPrompt(block: MobileBlock, runInput: string): string {
  if (block.kind === 'main' && runInput.trim().length > 0) return runInput.trim();
  return block.summary ?? '';
}

/** The first line of the text that has something on it. */
function firstLine(text: string): string {
  return text.split('\n').find((line) => line.trim().length > 0) ?? '';
}

/**
 * The one brief line a folded block shows under its title: the latest step while it is
 * running, else the first line of what it produced, else what it was asked.
 */
export function blockGist(block: MobileBlock, runInput: string): string {
  const latest = inMotion(block) ? block.activity?.[block.activity.length - 1] : undefined;
  if (latest) return runPreview(latest);
  const result = runPreview(firstLine(block.output ?? ''));
  if (result.length > 0) return result;
  return runPreview(blockPrompt(block, runInput));
}

/** The steps an open block lists: only while it is still in motion. */
export function blockActivity(block: MobileBlock): string[] {
  return inMotion(block) ? (block.activity ?? []) : [];
}

/** Lines a long final result shows before it asks to be opened in full. */
export const RESULT_PREVIEW_LINES = 15;
/** Characters the same preview keeps when a few lines are very long ones. */
export const RESULT_PREVIEW_CHARS = 1_500;

/**
 * A settled run's final answer: the full `result` the Mac sends for its newest run,
 * else the request block's shorter output (an older run, or an older Mac), else
 * nothing — a run still in motion, or one that said nothing, gets no result card.
 */
export function runResult(run: MobileMightyRun): string | undefined {
  if (run.status !== 'completed' && run.status !== 'error' && run.status !== 'stopped') return undefined;
  const text = run.result ?? run.blocks.find((block) => block.kind === 'main')?.output ?? '';
  return text.trim().length > 0 ? text : undefined;
}

/**
 * The head of a long result, cut at `RESULT_PREVIEW_LINES` lines or
 * `RESULT_PREVIEW_CHARS` characters, whichever comes first. `folded` says whether
 * anything was left out, so the card offers the rest only when there is a rest.
 */
export function resultPreview(text: string): { preview: string; folded: boolean } {
  const head = text.split('\n').slice(0, RESULT_PREVIEW_LINES).join('\n').slice(0, RESULT_PREVIEW_CHARS);
  // A cut through an emoji would leave half of it behind as a broken glyph.
  const preview = head.replace(/[\uD800-\uDBFF]$/, '').trimEnd();
  return preview.length < text.trimEnd().length ? { preview: `${preview}…`, folded: true } : { preview: text, folded: false };
}

// ------------------------------------------------------------------ parsing

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function arrayOf(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function parseBlock(raw: unknown): MobileBlock | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const block: MobileBlock = {
    id: raw.id,
    kind: inlineText(raw.kind, 40),
    title: inlineText(raw.title, 160),
    status: inlineText(raw.status, 40),
  };
  const summary = inlineText(raw.summary, MAX_BLOCK_SUMMARY);
  if (summary.length > 0) block.summary = summary;
  const activity = arrayOf(raw.activity)
    .map((line) => inlineText(line, MAX_BLOCK_SUMMARY))
    .filter((line) => line.length > 0)
    .slice(-MAX_BLOCK_ACTIVITY);
  if (activity.length > 0) block.activity = activity;
  const output = blockText(raw.output, MAX_BLOCK_OUTPUT);
  if (output.length > 0) block.output = output;
  if (typeof raw.durationMs === 'number' && Number.isFinite(raw.durationMs) && raw.durationMs >= 0) {
    block.durationMs = raw.durationMs;
  }
  const nodeModelLabel = inlineText(raw.nodeModelLabel, 240);
  if (nodeModelLabel.length > 0) block.nodeModelLabel = nodeModelLabel;
  return block;
}

function parseRun(raw: unknown): MobileMightyRun | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const parsed = arrayOf(raw.blocks)
    .map(parseBlock)
    .filter((block): block is MobileBlock => block !== undefined);
  const run: MobileMightyRun = {
    id: raw.id,
    input: typeof raw.input === 'string' ? raw.input : '',
    status: inlineText(raw.status, 40),
    blocks: parsed.slice(-MAX_BLOCKS_PER_RUN),
  };
  if (parsed.length > MAX_BLOCKS_PER_RUN) {
    run.omittedBlocks = parsed.length - MAX_BLOCKS_PER_RUN;
  }
  const title = inlineText(raw.title, 160);
  if (title.length > 0) run.title = title;
  const result = blockText(raw.result, MAX_RUN_RESULT);
  if (result.trim().length > 0) run.result = result;
  return run;
}

/**
 * Reads `MobileSessionDetail.mighty`. Returns undefined when the host sent nothing we
 * can draw; otherwise every list is present (possibly empty) and every string is safe
 * to render.
 */
export function normalizeMighty(raw: unknown): MobileMighty | undefined {
  if (!isRecord(raw)) return undefined;
  const mighty: MobileMighty = {
    style: inlineText(raw.style, 40),
    runs: arrayOf(raw.runs)
      .map(parseRun)
      .filter((run): run is MobileMightyRun => run !== undefined)
      .slice(-MAX_RUNS),
  };
  const styleId = inlineText(raw.styleId, 40);
  if (styleId.length > 0) mighty.styleId = styleId;
  const panel = normalizeStylePanel(raw.panel);
  if (panel) mighty.panel = panel;
  // A panel that arrived and could not be read is not the same as no panel at all:
  // `panelOf` says so on screen rather than falling back to the legacy payload.
  else if (raw.panel !== undefined && raw.panel !== null) mighty.panelUnreadable = true;
  const ouroboros = normalizeLegacyOuroboros(raw.ouroboros);
  if (ouroboros) mighty.ouroboros = ouroboros;
  const paperthin = normalizeLegacyPaperthin(raw.paperthin);
  if (paperthin) mighty.paperthin = paperthin;
  return mighty;
}

/**
 * The session view the phone opens by default for a pane. Non-shell panes always
 * carry a mighty payload from the Mac, so when one is present the phone opens blocks.
 * The user's explicit toggle overrides this; the Mac pane's own view mode does not.
 */
export function defaultView(mighty: MobileMighty | undefined): 'log' | 'blocks' {
  return mighty !== undefined ? 'blocks' : 'log';
}
