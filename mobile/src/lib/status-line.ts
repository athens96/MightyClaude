import { MAX_STATUS_LINES, type RateLimit, type StatusSegment } from '@/api/types';

/** Only `#RRGGBB` is accepted; anything else could be an arbitrary style string. */
const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;

export function isHexColor(value: unknown): value is string {
  return typeof value === 'string' && HEX_COLOR.test(value);
}

/** Longest segment text kept; a status line is one short row, not a document. */
export const MAX_SEGMENT_TEXT = 256;
/** Most segments kept per line. */
export const MAX_LINE_SEGMENTS = 40;

/**
 * C0 and C1 control characters plus the bidi overrides (U+200E, U+200F, U+202A–U+202E,
 * U+2066–U+2069). Both can reorder or hide what the row appears to say, and neither
 * belongs in a single row of status text.
 */
const UNSAFE_TEXT = /[\u0000-\u001F\u007F-\u009F\u200E\u200F\u202A-\u202E\u2066-\u2069]/g;

/** Strips what must not be rendered and cuts the rest to `MAX_SEGMENT_TEXT`. */
export function sanitiseSegmentText(text: string): string {
  return text.replace(UNSAFE_TEXT, '').slice(0, MAX_SEGMENT_TEXT);
}

function sanitiseSegment(raw: unknown): StatusSegment | undefined {
  if (raw === null || typeof raw !== 'object') return undefined;
  const candidate = raw as { text?: unknown; fg?: unknown; bold?: unknown };
  if (typeof candidate.text !== 'string') return undefined;
  const text = sanitiseSegmentText(candidate.text);
  if (text.length === 0) return undefined;
  const segment: StatusSegment = { text };
  if (isHexColor(candidate.fg)) segment.fg = candidate.fg;
  if (candidate.bold === true) segment.bold = true;
  return segment;
}

/**
 * Turns the host's `statusLine` into something safe to render: at most six lines of at
 * most forty segments, text without control or bidi characters and no longer than
 * `MAX_SEGMENT_TEXT`, and colours dropped unless they are `#RRGGBB`.
 */
export function sanitiseStatusLines(raw: unknown): StatusSegment[][] {
  if (raw === null || typeof raw !== 'object') return [];
  const lines = (raw as { lines?: unknown }).lines;
  if (!Array.isArray(lines)) return [];
  const result: StatusSegment[][] = [];
  for (const line of lines) {
    if (result.length >= MAX_STATUS_LINES) break;
    if (!Array.isArray(line)) continue;
    const segments = line
      .map(sanitiseSegment)
      .filter((segment): segment is StatusSegment => segment !== undefined)
      .slice(0, MAX_LINE_SEGMENTS);
    if (segments.length > 0) result.push(segments);
  }
  return result;
}

/** Keeps labelled bars only, with the percentage clamped to 0..100. */
export function sanitiseRateLimits(raw: unknown): RateLimit[] {
  if (!Array.isArray(raw)) return [];
  const result: RateLimit[] = [];
  for (const entry of raw) {
    if (entry === null || typeof entry !== 'object') continue;
    const candidate = entry as { label?: unknown; usedPercent?: unknown; resetsAt?: unknown };
    if (typeof candidate.label !== 'string' || candidate.label.length === 0) continue;
    if (typeof candidate.usedPercent !== 'number' || !Number.isFinite(candidate.usedPercent)) {
      continue;
    }
    const limit: RateLimit = {
      label: candidate.label,
      usedPercent: Math.min(100, Math.max(0, candidate.usedPercent)),
    };
    if (typeof candidate.resetsAt === 'string' && candidate.resetsAt.length > 0) {
      limit.resetsAt = candidate.resetsAt;
    }
    result.push(limit);
  }
  return result;
}
