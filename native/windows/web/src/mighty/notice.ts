// Finished background work gets a diagram of its own (Core BackgroundNoticeRuns, macOS BackgroundNoticeRuns): when a task
// notification or a sub-agent's hand-back opens a Claude turn, the host records that turn as a run `<original>:notice:<n>`,
// flagged `noticeRun`, whose children copy the finished background blocks. The request that started the work keeps those
// blocks as data, but no view draws them there. A notice run is no request: it takes no number and never the newest
// request's place. Pure; the same rules as Core MightyGraphSupport.
import type { MightyGraphAgent, MightyGraphRun } from '../store/models';

const ended = (status: string) => status === 'completed' || status === 'error' || status === 'stopped';

/** The agents a view draws for a run: background blocks only inside a notice run, and the blocks under a hidden one with it. */
export function shownAgents(run: MightyGraphRun): MightyGraphAgent[] {
  if (run.noticeRun === true) return run.agents;
  const hidden = new Set(run.agents.filter((agent) => agent.background === true).map((agent) => agent.id));
  if (hidden.size === 0) return run.agents;
  for (let grew = true; grew;) {
    grew = false;
    for (const agent of run.agents) {
      if (!hidden.has(agent.id) && agent.parentID !== undefined && hidden.has(agent.parentID)) { hidden.add(agent.id); grew = true; }
    }
  }
  return run.agents.filter((agent) => !hidden.has(agent.id));
}

/** The run as views draw it: the same object when nothing is hidden. */
export function shownRun(run: MightyGraphRun): MightyGraphRun {
  const agents = shownAgents(run);
  return agents.length === run.agents.length ? run : { ...run, agents };
}

/** The runs as views draw them; the same list when no run holds a background block. */
export function shownRuns(runs: readonly MightyGraphRun[]): readonly MightyGraphRun[] {
  return runs.some((run) => run.agents.some((agent) => agent.background === true)) ? runs.map(shownRun) : runs;
}

/** A notice run taken into a running turn: it opened complete and has no reply. No view follows it or gives it a result card. */
export const quietNotice = (run: MightyGraphRun) => run.noticeRun === true && run.status === 'completed' && (run.finalOutput ?? '').trim() === '';

/** A notice run a later notice turn answers in again: it keeps showing its earlier reply meanwhile. */
export const reopenedWithReply = (run: MightyGraphRun) => run.noticeRun === true && !ended(run.status) && (run.finalOutput ?? '') !== '';

/** Where the user's newest request is: the last run that is not a notice run; null without one. */
export function newestRequestIndex(runs: readonly MightyGraphRun[]): number | null {
  for (let index = runs.length - 1; index >= 0; index--) if (runs[index]!.noticeRun !== true) return index;
  return null;
}

/** The user's newest request. */
export function newestRequestRun(runs: readonly MightyGraphRun[]): MightyGraphRun | undefined {
  const index = newestRequestIndex(runs);
  return index === null ? undefined : runs[index];
}

/**
 * The run the views follow as the newest work: the newest request, or a notice run after it that is a turn of its own (running,
 * or holding Claude's reply). A quiet notice run never takes the request's place.
 */
export function followRunIndex(runs: readonly MightyGraphRun[]): number | null {
  const newest = newestRequestIndex(runs) ?? -1;
  for (let index = runs.length - 1; index >= 0; index--) {
    const run = runs[index]!;
    if (index === newest || (run.noticeRun === true && index > newest && !quietNotice(run))) return index;
  }
  return null;
}

/** Each run's request number: notice runs add none and carry the number of the request before them (at least 1). */
export function requestOrdinals(runs: readonly MightyGraphRun[]): number[] {
  let count = 0;
  return runs.map((run) => {
    if (run.noticeRun !== true) count += 1;
    return Math.max(1, count);
  });
}
