import type {
  MobileAdvisor,
  MobileAdvisorRow,
  MobileBlockCard,
  MobileBlockRole,
  MobileDelegateLine,
  MobileFlowLog,
  MobileFlowLogRow,
  MobileInkedText,
  MobileReturn,
} from '@/api/types';
import { t } from '@/lib/i18n';
import { elapsedText, inlineText } from '@/lib/styles';
import type { Palette } from '@/theme';

/**
 * The role view as the phone reads it (docs/mobile-remote.md "역할 보기"): who each block is, a compact row's figures, the delegate
 * line, the way back, the advisor and the session log, all worded by the host (the Mac's `MobileFlow`, Windows' `MobileFlow.cs`).
 * The phone only lays them out, maps the inks onto its palette and keeps a running row's time against its own clock. Everything here
 * is pure, and reads the payload defensively: a field it adds later, or one it leaves out, never crashes a screen.
 */

/** The longest words kept from the host: a line the screen cuts to its width anyway. */
const MAX_LINE = 400;
const MAX_WORD = 160;
/** The most consultations kept from one run (the host keeps 32), and the most a run's advisor card lists. */
export const MAX_ADVISOR_ROWS = 32;
export const ADVISOR_ROWS_SHOWN = 3;
/** The most log rows kept (the desktops show five). */
export const MAX_LOG_ROWS = 10;
/** The marks a block may carry, in the desktops' order. */
const MARKS = ['background', 'fork', 'worktree'] as const;

function record(value: unknown): Record<string, unknown> | undefined {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function words(value: unknown, limit = MAX_WORD): string[] {
  return Array.isArray(value) ? value.map((item) => inlineText(item, limit)).filter((item) => item.length > 0).slice(0, 8) : [];
}

/** A time on the wire: milliseconds since 1970, a finite number from 0. */
function moment(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
}

function count(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 ? Math.min(value, 9_999) : undefined;
}

function inked(value: unknown): MobileInkedText | undefined {
  const item = record(value);
  const text = inlineText(item?.text, MAX_LINE);
  return item && text.length > 0 ? { text, ink: inlineText(item.ink, 40) } : undefined;
}

function inkedList(value: unknown): MobileInkedText[] {
  return Array.isArray(value) ? value.map(inked).filter((part): part is MobileInkedText => part !== undefined).slice(0, 8) : [];
}

export function parseRole(raw: unknown): MobileBlockRole | undefined {
  const value = record(raw);
  const name = inlineText(value?.name, 40);
  if (!value || name.length === 0) return undefined;
  const role: MobileBlockRole = { name, ink: inlineText(value.ink, 40), summary: inlineText(value.summary, MAX_LINE) };
  const identity = inlineText(value.identity, MAX_LINE);
  if (identity.length > 0) role.identity = identity;
  const marks = words(value.marks, 40).filter((mark): mark is (typeof MARKS)[number] => (MARKS as readonly string[]).includes(mark));
  if (marks.length > 0) role.marks = marks;
  return role;
}

export function parseCard(raw: unknown): MobileBlockCard | undefined {
  const value = record(raw);
  if (!value) return undefined;
  const card: MobileBlockCard = { before: words(value.before), after: words(value.after) };
  const activity = inlineText(value.activity, MAX_LINE);
  if (activity.length > 0) card.activity = activity;
  const startedAt = moment(value.startedAt);
  if (startedAt !== undefined) card.startedAt = startedAt;
  const endedAt = moment(value.endedAt);
  if (endedAt !== undefined) card.endedAt = endedAt;
  const nested = count(value.nested);
  if (nested !== undefined) card.nested = nested;
  const copy = record(value.copy);
  const runId = inlineText(copy?.runId, 200);
  const blockId = inlineText(copy?.blockId, 200);
  if (runId.length > 0 && blockId.length > 0) card.copy = { runId, blockId };
  return card;
}

export function parseDelegate(raw: unknown): MobileDelegateLine | undefined {
  const value = record(raw);
  const head = inlineText(value?.head, MAX_LINE);
  if (!value || head.length === 0) return undefined;
  const line: MobileDelegateLine = { head, text: inlineText(value.text, MAX_LINE) };
  const parts = inkedList(value.parts);
  if (parts.length > 0) line.parts = parts;
  const model = inlineText(value.model, MAX_WORD);
  if (model.length > 0) line.model = model;
  const progress = inlineText(value.progress, MAX_WORD);
  if (progress.length > 0) line.progress = progress;
  if (line.text.length === 0) line.text = delegateText(line);
  return line;
}

export function parseReturning(raw: unknown): MobileReturn | undefined {
  const value = record(raw);
  const text = inlineText(value?.text, MAX_LINE);
  return value && text.length > 0 ? { text, waiting: value.waiting === true } : undefined;
}

function parseAdvisorRow(raw: unknown): MobileAdvisorRow | undefined {
  const value = record(raw);
  const id = inlineText(value?.id, 200);
  if (!value || id.length === 0) return undefined;
  const row: MobileAdvisorRow = {
    id,
    caller: inlineText(value.caller, MAX_WORD),
    ink: inlineText(value.ink, 40),
    line: inlineText(value.line, MAX_LINE),
    state: inlineText(value.state, 40),
  };
  const at = moment(value.at);
  if (at !== undefined) row.at = at;
  if (typeof value.seconds === 'number' && Number.isInteger(value.seconds) && value.seconds >= 0) row.seconds = value.seconds;
  return row;
}

export function parseAdvisor(raw: unknown): MobileAdvisor | undefined {
  const value = record(raw);
  const header = inlineText(value?.header, MAX_LINE);
  if (!value || header.length === 0) return undefined;
  const rows = (Array.isArray(value.rows) ? value.rows : [])
    .map(parseAdvisorRow)
    .filter((row): row is MobileAdvisorRow => row !== undefined)
    .slice(-MAX_ADVISOR_ROWS);
  const calls = typeof value.calls === 'number' && Number.isInteger(value.calls) ? Math.max(rows.length, Math.min(value.calls, 9_999)) : rows.length;
  const advisor: MobileAdvisor = { header, rows, calls, totals: inlineText(value.totals, MAX_LINE) };
  const state = inlineText(value.state, 40);
  if (state.length > 0) advisor.state = state;
  const note = inlineText(value.note, MAX_LINE);
  if (note.length > 0) advisor.note = note;
  const empty = inlineText(value.empty, MAX_LINE);
  if (empty.length > 0) advisor.empty = empty;
  return advisor;
}

export function parseFlowLog(raw: unknown): MobileFlowLog | undefined {
  const value = record(raw);
  const status = inkedList(value?.status);
  if (!value || status.length === 0) return undefined;
  const rows = (Array.isArray(value.rows) ? value.rows : [])
    .map((entry): MobileFlowLogRow | undefined => {
      const row = record(entry);
      const id = inlineText(row?.id, 300);
      const at = moment(row?.at);
      const what = inlineText(row?.what, MAX_LINE);
      if (!row || id.length === 0 || at === undefined || what.length === 0) return undefined;
      return { id, at, who: inlineText(row.who, MAX_WORD), ink: inlineText(row.ink, 40), what };
    })
    .filter((row): row is MobileFlowLogRow => row !== undefined)
    .slice(-MAX_LOG_ROWS);
  return { status, rows };
}

// ------------------------------------------------------------------ words and inks

/**
 * A design token's colour on the phone's palette: the desktops' role inks (`taskText` the lead's teal, `agentText` a sub-agent's
 * violet, `ink2` a background command's grey, `steerText` the advisor's pink) and a custom agent's colours, the same values
 * (native/contracts/fixtures/design-tokens.json). A token this app does not know reads as the neutral ink.
 */
export function inkColor(palette: Palette, ink: string): string {
  switch (ink) {
    case 'taskText':
      return palette.blockTask;
    case 'agentText':
      return palette.blockAgent;
    case 'steerText':
      return palette.blockSteer;
    case 'accent':
      return palette.accent;
    case 'doneText':
      return palette.success;
    case 'errText':
      return palette.danger;
    case 'waitText':
      return palette.warning;
    case 'questionText':
      return palette.blockQuestion;
    default:
      return palette.textMuted;
  }
}

/** A mark in words (`graph.identity.word.*`): 백그라운드, 포크, worktree. */
export function markWord(mark: string): string | undefined {
  return (MARKS as readonly string[]).includes(mark) ? t(`graph.identity.word.${mark}`) : undefined;
}

/**
 * A block's one line of identity: "Explore · Haiku 5.5 · 백그라운드 · worktree". Empty when it says nothing. A background command's
 * line is its command alone ("작업 · npm test"): every command of the role view runs in the background, so the word adds nothing.
 */
export function identityLine(role: MobileBlockRole | undefined): string {
  if (!role) return '';
  const marks = (role.marks ?? [])
    .filter((mark) => !(role.name === 'task' && mark === 'background'))
    .map(markWord)
    .filter((word): word is string => word !== undefined);
  return [role.identity, ...marks].filter((part): part is string => part !== undefined && part.length > 0).join(' · ');
}

/** Whether a block still runs, so its row's time keeps moving. */
export function stillRunning(status: string): boolean {
  return status === 'running' || status === 'waiting';
}

/**
 * A compact row's time: while it runs, from its start to now (on the phone's clock); once it ended, how long it ran (`45초`, `3분 12초`,
 * as the desktops' `FlowCard.elapsed`). Undefined when the host saw no start, or no end of an ended row.
 */
export function cardTime(card: Pick<MobileBlockCard, 'startedAt' | 'endedAt'>, status: string, now: number): string | undefined {
  if (card.startedAt === undefined) return undefined;
  if (stillRunning(status)) return elapsedText(card.startedAt, now);
  return card.endedAt !== undefined && card.endedAt >= card.startedAt ? elapsedText(card.startedAt, card.endedAt) : undefined;
}

/** A compact row's last line: "실행 중 · 42초 · 도구 3 · 토큰 12K". */
export function cardFigures(card: MobileBlockCard, status: string, now: number): string {
  const time = cardTime(card, status, now);
  return [...card.before, ...(time !== undefined ? [time] : []), ...card.after].join(' · ');
}

/** A delegate line as one sentence: its head, its parts, its model and its progress. */
export function delegateText(line: Pick<MobileDelegateLine, 'head' | 'parts' | 'model' | 'progress'>): string {
  return [line.head, ...(line.parts ?? []).map((part) => part.text), line.model, line.progress]
    .filter((part): part is string => part !== undefined && part.length > 0)
    .join(' · ');
}

/** A time as the log writes it: hours, minutes and seconds on the phone's own clock (the reader's time zone). */
export function logClock(at: number): string {
  const date = new Date(at);
  const two = (value: number) => String(value).padStart(2, '0');
  return `${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}`;
}

/** When a consultation was asked and how long its reply took: "10:03:10 · 7초". */
export function advisorWhen(row: Pick<MobileAdvisorRow, 'at' | 'seconds'>): string {
  const parts: string[] = [];
  if (row.at !== undefined) parts.push(logClock(row.at));
  if (row.seconds !== undefined) parts.push(elapsedText(0, row.seconds * 1_000));
  return parts.join(' · ');
}

/** The consultations the card lists, the newest last, and how many came before them ("이전 호출 N개"). */
export function advisorShown(advisor: MobileAdvisor, limit = ADVISOR_ROWS_SHOWN): { rows: MobileAdvisorRow[]; earlier: number } {
  const rows = advisor.rows.slice(-limit);
  return { rows, earlier: Math.max(0, advisor.calls - rows.length) };
}

/** The status line as one sentence, for a screen reader. */
export function statusText(log: MobileFlowLog): string {
  return log.status.map((part) => part.text).join(' · ');
}
