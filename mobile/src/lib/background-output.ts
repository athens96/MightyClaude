import { t } from '@/lib/i18n';
import { blockText } from '@/lib/styles';

/**
 * What a host sends of a background run's output file and how the phone shows it
 * (native/contracts/fixtures/background-output.json; macOS BackgroundOutputTail, web mighty/background.ts).
 * The phone never reads the file: `outputTail` arrives already cut to at most `maxLines` lines.
 */
export const BACKGROUND_OUTPUT = { maxLines: 6 } as const;

/** Lines a folded task shows while it runs, and once it ended; opened, all `maxLines`. */
export const FOLDED_TAIL_RUNNING = 3;
export const FOLDED_TAIL_ENDED = 2;
/** Lines of Claude's reply a folded task shows. */
export const FOLDED_REPLY_LINES = 2;

/** The word a status ends in, or null while the work is still in motion. */
function statusWord(status: string): string | null {
  switch (status) {
    case 'completed':
      return t('graph.block.taskNotice.completed');
    case 'error':
    case 'failed':
      return t('graph.block.taskNotice.failed');
    case 'stopped':
    case 'cancelled':
    case 'interrupted':
      return t('graph.block.taskNotice.stopped');
    case 'unknown':
      return t('styles.state.taskStatus.unknown');
    default:
      return null;
  }
}

/** Whether a status is one the work ended in; unknown is not one, as on the other hosts' `terminal`. */
export function backgroundEnded(status: string): boolean {
  return statusWord(status) !== null && status !== 'unknown';
}

/**
 * The kind line of a background block: a background run (a task block) or a background
 * sub-agent, waiting or ended. Null for every other block, a steer included.
 */
export function backgroundLabel(block: { kind: string; background?: boolean; status: string }): string | null {
  const run = block.kind === 'task';
  if (!run && !(block.background && block.kind !== 'steer')) return null;
  const word = statusWord(block.status);
  if (word === null) return t(run ? 'graph.background.runWaiting' : 'graph.background.agentWaiting');
  return t(run ? 'graph.background.runEnded' : 'graph.background.agentEnded', { status: word });
}

/** The output lines a task shows: the last three while it runs, two once it ended, all (at most six) when opened. */
export function shownTail(tail: readonly string[] | undefined, status: string, open: boolean): string[] {
  const lines = tail ?? [];
  return lines.slice(-(open ? BACKGROUND_OUTPUT.maxLines : backgroundEnded(status) ? FOLDED_TAIL_ENDED : FOLDED_TAIL_RUNNING));
}

/** Longest output line the phone keeps: the host's 200 Unicode scalars plus the cut mark, with room for wide ones. */
const MAX_TAIL_LINE = 420;

/** The background fields of a block or structure node, read defensively; a host that predates them sends none. */
export function parseBackground(
  raw: { background?: unknown; outputTail?: unknown; noticeReply?: unknown },
  replyLimit: number,
): { background?: true; outputTail?: string[]; noticeReply?: string } {
  const read: { background?: true; outputTail?: string[]; noticeReply?: string } = {};
  if (raw.background === true) read.background = true;
  if (Array.isArray(raw.outputTail)) {
    const lines = raw.outputTail
      .filter((line): line is string => typeof line === 'string')
      .map((line) => blockText(line, MAX_TAIL_LINE).replace(/[\r\n]+/g, ' '))
      .slice(-BACKGROUND_OUTPUT.maxLines);
    if (lines.some((line) => line.trim().length > 0)) read.outputTail = lines;
  }
  const reply = blockText(raw.noticeReply, replyLimit);
  if (reply.trim().length > 0) read.noticeReply = reply;
  return read;
}
