import type {
  MobileStructure,
  StructureActivity,
  StructureCluster,
  StructureColumn,
  StructureItem,
  StructureNode,
  StructureRow,
  StructureStep,
} from '@/api/types';
import { t } from '@/lib/i18n';
import { blockText, inlineText } from '@/lib/styles';

/**
 * The structure view (구조): the Mac's own `MightyStructure` of a pane, sent as
 * `MobileMighty.structure`. A styled pane is its phases, each holding the requests that
 * belong to it; a pane without a style is one row per request (요청 → 실행 → 결과).
 * The phone never works a phase or a step out for itself: it draws what the Mac sent.
 *
 * Everything here is pure. The payload is read defensively — an unknown version or shape
 * is reported rather than guessed at, a field of the wrong type is dropped, and every list
 * and text is clamped — and the screen's choices (which page opens, how a chip looks,
 * where a long text folds) are worked out here so they can be tested.
 */

/** The version of the payload this app reads. */
export const STRUCTURE_VERSION = 1;

/** Clamps, a little over what the Mac sends, so a host that grows still draws. */
export const MAX_STRUCTURE_COLUMNS = 24;
export const MAX_STRUCTURE_ITEMS = 20;
export const MAX_STRUCTURE_NODES = 60;
export const MAX_STRUCTURE_DEPTH = 6;
export const MAX_STRUCTURE_ACTIVITY = 5;
export const MAX_STRUCTURE_REQUEST = 1_024;
export const MAX_STRUCTURE_MESSAGE = 1_024;
export const MAX_STRUCTURE_ANSWER = 4_096;
export const MAX_STRUCTURE_PROMPT = 300;
export const MAX_STRUCTURE_OUTPUT = 1_024;
export const MAX_STRUCTURE_LINE = 200;
export const MAX_STRUCTURE_TITLE = 160;

/**
 * What the 구조 view can draw:
 * - `ok`: a structure;
 * - `none`: the Mac has nothing to draw (no request yet, no style phase);
 * - `unreadable`: the Mac sent something this app cannot read (a newer version);
 * - `unsupported`: the Mac predates the structure view (no "structure" capability);
 * - `loading`: asked for, not answered yet.
 */
export type StructureState =
  | { kind: 'ok'; structure: MobileStructure }
  | { kind: 'none' }
  | { kind: 'unreadable' }
  | { kind: 'unsupported' }
  | { kind: 'loading' };

/** The structure the phone holds and the Mac's tag for it, which it echoes back. */
export interface HeldStructure {
  tag: number;
  state: StructureState;
}

// ------------------------------------------------------------------ parsing

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function arrayOf(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function count(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.floor(value) : 0;
}

function optionalText(value: unknown, limit: number): string | undefined {
  const text = blockText(value, limit);
  return text.trim().length > 0 ? text : undefined;
}

function optionalLine(value: unknown, limit: number): string | undefined {
  const text = inlineText(value, limit);
  return text.length > 0 ? text : undefined;
}

const STEPS: readonly StructureStep[] = ['done', 'current', 'pending', 'error', 'stopped'];

/** A step word the host may add later reads as pending: grey, claiming nothing. */
export function parseStep(value: unknown): StructureStep {
  return STEPS.find((step) => step === value) ?? 'pending';
}

function parseActivity(raw: unknown): StructureActivity | undefined {
  if (!isRecord(raw)) return undefined;
  const text = inlineText(raw.text, MAX_STRUCTURE_LINE);
  if (text.length === 0) return undefined;
  return { kind: inlineText(raw.kind, 40), state: inlineText(raw.state, 40), text };
}

function parseNode(raw: unknown): StructureNode | undefined {
  if (!isRecord(raw) || typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const node: StructureNode = {
    id: raw.id,
    depth: Math.min(MAX_STRUCTURE_DEPTH, count(raw.depth)),
    title: inlineText(raw.title, MAX_STRUCTURE_TITLE),
    status: inlineText(raw.status, 40),
    kind: inlineText(raw.kind, 40),
  };
  const model = optionalLine(raw.model, 80);
  if (model) node.model = model;
  const prompt = optionalText(raw.prompt, MAX_STRUCTURE_PROMPT);
  if (prompt) node.prompt = prompt;
  const output = optionalText(raw.output, MAX_STRUCTURE_OUTPUT);
  if (output) node.output = output;
  const activity = parseActivity(raw.activity);
  if (activity) node.activity = activity;
  return node;
}

function parseItem(raw: unknown): StructureItem | undefined {
  if (!isRecord(raw) || typeof raw.runId !== 'string' || raw.runId.length === 0) return undefined;
  const tally = isRecord(raw.tally) ? raw.tally : {};
  let budget = MAX_STRUCTURE_NODES;
  let dropped = 0;
  const clusters: StructureCluster[] = [];
  for (const cluster of arrayOf(raw.clusters)) {
    const nodes = arrayOf(isRecord(cluster) ? cluster.nodes : undefined)
      .map(parseNode)
      .filter((node): node is StructureNode => node !== undefined);
    const kept = nodes.slice(0, budget);
    dropped += nodes.length - kept.length;
    budget -= kept.length;
    if (kept.length > 0) clusters.push({ nodes: kept });
  }
  const item: StructureItem = {
    runId: raw.runId,
    ordinal: Math.max(1, count(raw.ordinal)),
    status: inlineText(raw.status, 40),
    finished: raw.finished === true,
    activity: arrayOf(raw.activity)
      .map(parseActivity)
      .filter((line): line is StructureActivity => line !== undefined)
      .slice(-MAX_STRUCTURE_ACTIVITY),
    tally: { agents: count(tally.agents), tools: count(tally.tools) },
    clusters,
  };
  const title = optionalLine(raw.title, MAX_STRUCTURE_TITLE);
  if (title) item.title = title;
  const request = optionalText(raw.request, MAX_STRUCTURE_REQUEST);
  if (request) item.request = request;
  const message = optionalText(raw.message, MAX_STRUCTURE_MESSAGE);
  if (message) item.message = message;
  const answer = optionalText(raw.answer, MAX_STRUCTURE_ANSWER);
  if (answer) item.answer = answer;
  const omittedNodes = count(raw.omittedNodes) + dropped;
  if (omittedNodes > 0) item.omittedNodes = omittedNodes;
  return item;
}

function parseColumn(raw: unknown): StructureColumn | undefined {
  if (!isRecord(raw) || typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const title = inlineText(raw.title, MAX_STRUCTURE_TITLE);
  return {
    id: raw.id,
    title: title.length > 0 ? title : raw.id.slice(0, MAX_STRUCTURE_TITLE),
    step: parseStep(raw.step),
    items: arrayOf(raw.items)
      .map(parseItem)
      .filter((item): item is StructureItem => item !== undefined),
  };
}

function parseRow(raw: unknown): StructureRow | undefined {
  if (!isRecord(raw)) return undefined;
  const item = parseItem(raw.item);
  if (!item) return undefined;
  return { item, request: parseStep(raw.request), execution: parseStep(raw.execution), result: parseStep(raw.result) };
}

/**
 * Keeps the newest `MAX_STRUCTURE_ITEMS` requests across every column, and counts the
 * rest. Requests that share a number are ranked by where they sit (later columns, then
 * lower in a column, count as newer), so the cap holds whatever the numbers say.
 */
function clampColumns(columns: StructureColumn[]): { columns: StructureColumn[]; dropped: number } {
  const ranked = columns.flatMap((column, columnIndex) =>
    column.items.map((item, position) => ({ item, columnIndex, position })),
  );
  if (ranked.length <= MAX_STRUCTURE_ITEMS) return { columns, dropped: 0 };
  ranked.sort((a, b) => b.item.ordinal - a.item.ordinal || b.columnIndex - a.columnIndex || b.position - a.position);
  const kept = new Set(ranked.slice(0, MAX_STRUCTURE_ITEMS).map((entry) => entry.item));
  const clamped = columns.map((column) => ({ ...column, items: column.items.filter((item) => kept.has(item)) }));
  return { columns: clamped, dropped: ranked.length - kept.size };
}

/**
 * Reads `MobileMighty.structure`. `none` when the host sent nothing (an older Mac, or a
 * pane with nothing to draw); `unreadable` when it sent something this app does not know
 * how to read; otherwise a structure whose every list and text is bounded and safe.
 */
export function normalizeStructure(raw: unknown): StructureState {
  if (raw === undefined || raw === null) return { kind: 'none' };
  if (!isRecord(raw) || raw.v !== STRUCTURE_VERSION) return { kind: 'unreadable' };
  const omitted = count(raw.omitted);
  if (raw.shape === 'phases') {
    const parsed = arrayOf(raw.columns)
      .map(parseColumn)
      .filter((column): column is StructureColumn => column !== undefined)
      .slice(0, MAX_STRUCTURE_COLUMNS);
    if (parsed.length === 0) return { kind: 'unreadable' };
    const { columns, dropped } = clampColumns(parsed);
    const structure: MobileStructure = { v: STRUCTURE_VERSION, shape: 'phases', columns };
    if (omitted + dropped > 0) structure.omitted = omitted + dropped;
    return { kind: 'ok', structure };
  }
  if (raw.shape === 'free') {
    if (!Array.isArray(raw.rows)) return { kind: 'unreadable' };
    const parsed = raw.rows
      .map(parseRow)
      .filter((row): row is StructureRow => row !== undefined);
    const rows = parsed.slice(-MAX_STRUCTURE_ITEMS);
    const structure: MobileStructure = { v: STRUCTURE_VERSION, shape: 'free', rows };
    const dropped = parsed.length - rows.length;
    if (omitted + dropped > 0) structure.omitted = omitted + dropped;
    return { kind: 'ok', structure };
  }
  return { kind: 'unreadable' };
}

// ------------------------------------------------------------------ what the phone holds

/**
 * The `structure` query value: the tag of what the phone holds, 0 for nothing. The Mac
 * leaves the structure out of its answer while the tag still matches.
 */
export function structureQuery(held: HeldStructure | undefined): number {
  return held?.tag ?? 0;
}

function isTag(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 999_999_999_999;
}

/**
 * Takes in one session detail's `mighty` as the Mac sent it (before `normalizeMighty`):
 * - no `structureTag`: the detail was not asked for the structure; nothing changes;
 * - tag 0: the Mac has nothing to draw;
 * - a `structure`: the new value, its unchanged requests kept as the same objects;
 * - the tag alone, matching: what the phone holds is current;
 * - the tag alone, not matching: the Mac shed it to fit the relay's frame. What the
 *   phone holds stays on screen and its old tag is sent again, so the next answer brings
 *   the new value once there is room.
 */
export function receiveStructure(held: HeldStructure | undefined, mighty: unknown): HeldStructure | undefined {
  if (!isRecord(mighty) || !isTag(mighty.structureTag)) return held;
  const tag = mighty.structureTag;
  if (tag === 0) return held?.tag === 0 ? held : { tag, state: { kind: 'none' } };
  if (mighty.structure !== undefined && mighty.structure !== null) {
    const state = normalizeStructure(mighty.structure);
    if (state.kind !== 'ok') return { tag, state };
    const previous = held?.state.kind === 'ok' ? held.state.structure : undefined;
    return { tag, state: { kind: 'ok', structure: stabilizeStructure(previous, state.structure) } };
  }
  return held;
}

/**
 * `next` with every request, column and row that did not change taken from `previous`,
 * so a card the update did not touch keeps its object and its memoised row skips the
 * render.
 */
export function stabilizeStructure(previous: MobileStructure | undefined, next: MobileStructure): MobileStructure {
  if (!previous) return next;
  const before = new Map(structureItems(previous).map((item) => [item.runId, item]));
  const keep = (item: StructureItem): StructureItem => {
    const old = before.get(item.runId);
    return old && JSON.stringify(old) === JSON.stringify(item) ? old : item;
  };
  if (next.shape === 'phases') {
    const columns = new Map(previous.shape === 'phases' ? previous.columns.map((column) => [column.id, column]) : []);
    return {
      ...next,
      columns: next.columns.map((column) => {
        const items = column.items.map(keep);
        const old = columns.get(column.id);
        const same =
          old !== undefined &&
          old.title === column.title &&
          old.step === column.step &&
          old.items.length === items.length &&
          old.items.every((item, index) => item === items[index]);
        return same ? old : { ...column, items };
      }),
    };
  }
  const rows = new Map(previous.shape === 'free' ? previous.rows.map((row) => [row.item.runId, row]) : []);
  return {
    ...next,
    rows: next.rows.map((row) => {
      const item = keep(row.item);
      const old = rows.get(item.runId);
      return old &&
        old.item === item &&
        old.request === row.request &&
        old.execution === row.execution &&
        old.result === row.result
        ? old
        : { ...row, item };
    }),
  };
}

/**
 * What the 구조 view shows instead of a structure. An older Mac sends none at all, which
 * only matters once the pane has requests — an empty pane is just empty.
 */
export function structureNotice(
  state: StructureState,
  hasRuns: boolean,
): 'needsMacUpdate' | 'needsAppUpdate' | 'empty' | 'loading' | undefined {
  switch (state.kind) {
    case 'ok':
      return undefined;
    case 'unreadable':
      return 'needsAppUpdate';
    case 'unsupported':
      return hasRuns ? 'needsMacUpdate' : 'empty';
    case 'loading':
      return 'loading';
    default:
      return 'empty';
  }
}

// ------------------------------------------------------------------ the phase pages

/**
 * The page the phase view opens on: the current phase, else the newest phase that holds
 * a request, else the first.
 */
export function currentPageIndex(columns: readonly StructureColumn[]): number {
  const current = columns.findIndex((column) => column.step === 'current');
  if (current >= 0) return current;
  for (let index = columns.length - 1; index >= 0; index -= 1) {
    if ((columns[index]?.items.length ?? 0) > 0) return index;
  }
  return 0;
}

/**
 * The page shown: the phase the user moved to (held by its id, so a phase list that
 * changes under it never shows another page), else the current phase's.
 */
export function visiblePage(columns: readonly StructureColumn[], chosen: string | undefined): number {
  if (columns.length === 0) return 0;
  const index = chosen === undefined ? -1 : columns.findIndex((column) => column.id === chosen);
  return index >= 0 ? index : currentPageIndex(columns);
}

/**
 * The choice a swipe or a tap leaves: none when it lands on the current phase, so the
 * pager follows the phase again from there; else the phase landed on.
 */
export function chosenAfterMove(columns: readonly StructureColumn[], landed: number): string | undefined {
  if (landed === currentPageIndex(columns)) return undefined;
  return columns[landed]?.id;
}

/** The page a horizontal pager rests on, from its offset and its page width. */
export function pageAtOffset(offsetX: number, pageWidth: number, pages: number): number {
  if (pageWidth <= 0 || pages <= 0) return 0;
  return Math.min(pages - 1, Math.max(0, Math.round(offsetX / pageWidth)));
}

/** "실행 (3/6)". */
export function pageLabel(columns: readonly StructureColumn[], index: number): string {
  const column = columns[index];
  if (!column) return '';
  return t('phone.structure.page', { name: column.title, index: index + 1, count: columns.length });
}

/** How a step looks: the colour family it takes and the mark it carries. */
export interface StepLook {
  /** `done` green, `current` the accent, `pending` grey; an ended request its own colour. */
  tone: 'done' | 'current' | 'pending' | 'err' | 'stop';
  mark: string;
}

export function stepLook(step: StructureStep): StepLook {
  switch (step) {
    case 'done':
      return { tone: 'done', mark: '✓' };
    case 'current':
      return { tone: 'current', mark: '●' };
    case 'error':
      return { tone: 'err', mark: '✕' };
    case 'stopped':
      return { tone: 'stop', mark: '■' };
    default:
      return { tone: 'pending', mark: '○' };
  }
}

/** The word for a step, read out with its name: "실행: 진행 중". */
export function stepAccessibility(name: string, step: StructureStep): string {
  const state =
    step === 'done'
      ? t('graph.structure.step.done')
      : step === 'current'
        ? t('graph.structure.step.current')
        : step === 'error'
          ? t('graph.state.error')
          : step === 'stopped'
            ? t('graph.state.stopped')
            : t('graph.structure.step.pending');
  return t('graph.structure.stepAccessibility', { name, state });
}

/** One chip of a rail. */
export interface RailChip {
  id: string;
  title: string;
  step: StructureStep;
  look: StepLook;
  label: string;
}

/** The phase bar over the pages. */
export function phaseRail(columns: readonly StructureColumn[]): RailChip[] {
  return columns.map((column) => ({
    id: column.id,
    title: column.title,
    step: column.step,
    look: stepLook(column.step),
    label: stepAccessibility(column.title, column.step),
  }));
}

/** A free request's own small rail: 요청 ─ 실행 ─ 결과. */
export function requestRail(row: StructureRow): RailChip[] {
  const steps: [string, string, StructureStep][] = [
    ['request', t('graph.structure.column.request'), row.request],
    ['execution', t('graph.structure.column.execution'), row.execution],
    ['result', t('graph.structure.column.result'), row.result],
  ];
  return steps.map(([id, title, step]) => ({
    id,
    title,
    step,
    look: stepLook(step),
    label: stepAccessibility(title, step),
  }));
}

/** "Seed · 요청 3", or just "요청 3". */
export function itemHeading(item: StructureItem): string {
  const request = t('remote.block.request', { ordinal: item.ordinal });
  return item.title ? `${item.title} · ${request}` : request;
}

/** The result part's heading, by how the request ended. */
export function resultHeading(item: StructureItem): string {
  if (item.status === 'error') return t('graph.block.resultError');
  if (item.status === 'stopped') return t('graph.block.resultStopped');
  return t('graph.block.result');
}

// ------------------------------------------------------------------ folding

/** Lines each part shows folded, as the Mac's structure cards do. */
export const FOLD_LINES = { request: 10, message: 8, answer: 15, prompt: 2, output: 6 } as const;
/** Characters a folded line may hold before a very long line folds too. */
export const FOLD_LINE_CHARS = 120;

/**
 * The head of a text, cut at `lines` lines or `lines × FOLD_LINE_CHARS` characters,
 * whichever comes first. `folded` says whether anything was left out, so the card offers
 * "전체 보기" only when there is more to see. A cut through an emoji's half is avoided,
 * and a Markdown head left inside a code fence gets the fence closed.
 */
export function foldText(text: string, lines: number, markdown = false): { preview: string; folded: boolean } {
  const trimmed = text.trimEnd();
  const head = trimmed.split('\n').slice(0, lines).join('\n').slice(0, lines * FOLD_LINE_CHARS);
  if (head.length >= trimmed.length) return { preview: trimmed, folded: false };
  let preview = `${head.replace(/[\uD800-\uDBFF]$/, '').trimEnd()}…`;
  if (markdown && openFence(preview)) preview += `\n${FENCE}`;
  return { preview, folded: true };
}

/** Three backticks, spelled out so no tool reads this file's own code as a fence. */
const FENCE = '\u0060\u0060\u0060';

/** Whether a Markdown text ends inside a backtick or tilde code block. */
export function openFence(markdown: string): boolean {
  let open: string | undefined;
  for (const line of markdown.split('\n')) {
    const match = /^ {0,3}(\u0060{3,}|~{3,})/.exec(line);
    if (!match?.[1]) continue;
    const run = match[1];
    if (open === undefined) open = run;
    else if (run[0] === open[0] && run.length >= open.length && line.trim() === run) open = undefined;
  }
  return open !== undefined;
}

/** Whether a node card has more to show than its folded rows. */
export function nodeFolds(node: StructureNode): boolean {
  return (
    (node.prompt !== undefined && foldText(node.prompt, FOLD_LINES.prompt).folded) ||
    (node.output !== undefined && foldText(node.output, FOLD_LINES.output).folded)
  );
}

/** "에이전트 · Opus 5.5": the node's kind and the model it reported. */
export function nodeMeta(node: StructureNode, kindLabel: (kind: string) => string): string {
  return [kindLabel(node.kind), node.model].filter((part): part is string => Boolean(part)).join(' · ');
}

/** Every request the structure holds, oldest first, for counting and lookups. */
export function structureItems(structure: MobileStructure): StructureItem[] {
  const items =
    structure.shape === 'phases' ? structure.columns.flatMap((column) => column.items) : structure.rows.map((row) => row.item);
  return [...items].sort((a, b) => a.ordinal - b.ordinal);
}

// ------------------------------------------------------------------ folded requests

/**
 * Finished requests fold, as on the Mac (`MightyStructure.Fold`): a request that completed,
 * failed or stopped shows as one compact card until the user opens it, a running or
 * waiting one is always open, and one that finishes folds by itself unless the user opened
 * it by hand. The hand-opened run ids are the only state the screen keeps.
 */
export function isCardFolded(item: StructureItem, unfolded: ReadonlySet<string>): boolean {
  return item.finished && !unfolded.has(item.runId);
}

/** The hand-opened cards after the user opens or folds `item`; an unfinished card has no control. */
export function toggleCardFold(item: StructureItem, unfolded: ReadonlySet<string>): ReadonlySet<string> {
  if (!item.finished) return unfolded;
  const next = new Set(unfolded);
  if (!next.delete(item.runId)) next.add(item.runId);
  return next;
}

/** The first line of `text` with words in it, without the Markdown that opens a line or wraps a word. */
export function firstTextLine(text: string): string | undefined {
  for (const raw of text.split(/\r?\n/)) {
    let line = raw.replace(/^[ \t]+/, '');
    if (line.startsWith('```')) continue;
    line = line.replace(/^(?:[#>] *)+/, '');
    line = line.replace(/^[-*+] /, '').replace(/^\d+\. /, '');
    const cleaned = line.replace(/\*\*|__|`/g, '').trim();
    if (cleaned.length > 0) return cleaned;
  }
  return undefined;
}

/** A folded card's request line: the request's first line. */
export function foldedRequestLine(item: StructureItem): string {
  return (item.request && firstTextLine(item.request)) ?? '';
}

/**
 * A folded card's result line: the answer's first line once the request completed with
 * one; undefined when it failed, stopped or completed without one, and the card says how
 * it ended instead.
 */
export function foldedResultLine(item: StructureItem): string | undefined {
  if (item.status !== 'completed' || !item.answer) return undefined;
  return firstTextLine(item.answer);
}
