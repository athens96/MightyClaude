import type { Translate } from '../i18n/locale';
import { terminal } from './layout';

/** The most output lines a task block or card shows when opened; folded, three while it runs and two once it ended (macOS BackgroundOutputTail). */
export const MaxTailLines = 6;

/**
 * The kind line under a background block's head: a background sub-agent or a background run, waiting or ended. Null for any
 * other block (macOS MightyGraphSupport.backgroundLabel, Core MightyGraphSupport.BackgroundLabel). A task block is a run, an agent
 * started in the background is a sub-agent; a status still in motion reads waiting.
 */
export function backgroundLabel(t: Translate, block: { kind?: string; background?: boolean; status: string }): string | null {
  const run = block.kind === 'task';
  if (!run && !(block.background && block.kind !== 'steer')) return null;
  const word = statusWord(t, block.status);
  if (word === null) return t(run ? 'graph.background.runWaiting' : 'graph.background.agentWaiting');
  return t(run ? 'graph.background.runEnded' : 'graph.background.agentEnded', { status: word });
}

function statusWord(t: Translate, status: string): string | null {
  switch (status) {
    case 'completed': return t('graph.block.taskNotice.completed');
    case 'error': case 'failed': return t('graph.block.taskNotice.failed');
    case 'stopped': case 'cancelled': case 'interrupted': return t('graph.block.taskNotice.stopped');
    case 'unknown': return t('styles.state.taskStatus.unknown');
    default: return null;
  }
}

/** The output lines a task shows: the last three while it runs, two once it ended, all of them (at most six) when opened. */
export function shownTail(tail: readonly string[] | undefined, status: string, open: boolean): string[] {
  const lines = tail ?? [];
  return lines.slice(-(open ? MaxTailLines : terminal(status) ? 2 : 3));
}

/** Whether a strip row opens to show more: its command (a run's, or a sub-agent's first prompt line) or its output lines (Core PlanCardSupport.BackgroundStripRow.Opens). */
export const backgroundRowOpens = (row: { command?: string; tailLines?: readonly string[] }) => row.command !== undefined || (row.tailLines?.length ?? 0) > 0;
