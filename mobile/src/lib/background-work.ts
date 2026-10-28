import type { MobileBackgroundWork } from '@/api/types';
import { BACKGROUND_OUTPUT, parseBackground } from '@/lib/background-output';
import { inlineText } from '@/lib/styles';

/**
 * Background work the phone lists above the Mighty view, and the rule for which blocks a run draws.
 * Mirrors the Mac's `MobileBackgroundWork` / `BackgroundNoticeRuns.shownAgents`
 * (docs/mobile-remote.md, "Background work and notice runs"). Everything here is pure.
 */

/** Most rows kept from one detail; the host lists the pane's running work, which is a handful. */
export const MAX_BACKGROUND_WORK = 50;
const MAX_TITLE = 1_000;
const MAX_COMMAND = 2_000;

/**
 * The running background work of a session detail, read defensively. A host that predates the field sends
 * none; a row that is not running (`status`), has no id, or names a kind this app does not know is dropped,
 * so the strip never shows ended work. A repeated id counts once.
 */
export function parseBackgroundWork(detail: { backgroundWork?: unknown } | undefined): MobileBackgroundWork[] {
  const raw = detail?.backgroundWork;
  if (!Array.isArray(raw)) return [];
  const rows: MobileBackgroundWork[] = [];
  const seen = new Set<string>();
  for (const entry of raw as unknown[]) {
    if (typeof entry !== 'object' || entry === null || Array.isArray(entry)) continue;
    const item = entry as Record<string, unknown>;
    if (typeof item.id !== 'string' || item.id.length === 0 || seen.has(item.id)) continue;
    if (item.kind !== 'agent' && item.kind !== 'run') continue;
    if (item.status !== 'running') continue;
    seen.add(item.id);
    const row: MobileBackgroundWork = {
      id: item.id,
      kind: item.kind,
      title: inlineText(item.title, MAX_TITLE),
      status: 'running',
      startedAt: typeof item.startedAt === 'string' ? item.startedAt : '',
    };
    const command = inlineText(item.command, MAX_COMMAND);
    if (command.length > 0) row.command = command;
    const { outputTail } = parseBackground({ outputTail: item.outputTail }, 0);
    if (outputTail) row.outputTail = outputTail;
    rows.push(row);
    if (rows.length === MAX_BACKGROUND_WORK) break;
  }
  return rows;
}

/** Milliseconds since `startedAt`, or undefined when the host's time cannot be read. */
export function backgroundElapsed(startedAt: string, now: number): number | undefined {
  const start = Date.parse(startedAt);
  return Number.isFinite(start) ? Math.max(0, now - start) : undefined;
}

/** The last output line that has something on it: the one line a folded row shows. */
export function backgroundLastLine(tail: readonly string[] | undefined): string {
  const lines = (tail ?? []).slice(-BACKGROUND_OUTPUT.maxLines).filter((line) => line.trim().length > 0);
  return lines[lines.length - 1]?.trim() ?? '';
}

/**
 * The blocks of a run a view draws: a background block only inside a notice run (the host already
 * leaves them out of a request; this covers a host that predates that).
 */
export function shownBlocks<T extends { background?: true }>(blocks: readonly T[], noticeRun: boolean): T[] {
  return noticeRun ? [...blocks] : blocks.filter((block) => block.background !== true);
}

/**
 * The nodes of one structure cluster a view draws, depth-first: as `shownBlocks`, and a node under a
 * hidden background node (deeper than it, until the depth comes back) goes with it.
 */
export function shownNodes<T extends { background?: true; depth: number }>(nodes: readonly T[], noticeRun: boolean): T[] {
  if (noticeRun) return [...nodes];
  const shown: T[] = [];
  let hiddenDepth: number | undefined;
  for (const node of nodes) {
    if (hiddenDepth !== undefined && node.depth > hiddenDepth) continue;
    hiddenDepth = undefined;
    if (node.background === true) {
      hiddenDepth = node.depth;
      continue;
    }
    shown.push(node);
  }
  return shown;
}
