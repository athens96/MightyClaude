// A streamed graph_run replaces the whole run record (the delta carries it by id), so every list in it is a new array even
// where nothing changed. The diagram keeps the previous objects wherever the values are the same, so a block memoized on
// its entries draws again only when they did change.
import type { LogEntry, MightyGraphAgent, MightyGraphRun } from '../store/models';

function sameEntry(a: LogEntry, b: LogEntry): boolean {
  if (a === b) return true;
  if (a.id !== b.id || a.text !== b.text || a.kind !== b.kind || a.timestamp !== b.timestamp || a.provider !== b.provider) return false;
  if (a.activity !== b.activity && JSON.stringify(a.activity) !== JSON.stringify(b.activity)) return false;
  return (a.images?.length ?? 0) === (b.images?.length ?? 0);
}

/** `next` with its entries kept from `previous` where they did not change; `previous` itself when none did. */
export function shareEntries(previous: readonly LogEntry[] | undefined, next: readonly LogEntry[]): readonly LogEntry[] {
  if (!previous || previous === next) return next;
  let changed = previous.length !== next.length;
  const out = next.map((entry, i) => {
    const before = previous[i];
    if (before && sameEntry(before, entry)) return before;
    changed = true;
    return entry;
  });
  return changed ? out : previous;
}

function shareAgent(previous: MightyGraphAgent | undefined, next: MightyGraphAgent): MightyGraphAgent {
  if (!previous || previous === next) return next;
  const entries = shareEntries(previous.entries, next.entries) as LogEntry[];
  const same = entries === previous.entries && previous.id === next.id && previous.parentID === next.parentID && previous.title === next.title
    && previous.input === next.input && previous.status === next.status && previous.kind === next.kind
    && JSON.stringify(previous.usage) === JSON.stringify(next.usage) && JSON.stringify(previous.responseRecords) === JSON.stringify(next.responseRecords);
  return same ? previous : { ...next, entries };
}

/** A run with its agents and entry lists kept from `previous` where they are the same. */
export function shareRun(previous: MightyGraphRun | undefined, next: MightyGraphRun): MightyGraphRun {
  if (!previous || previous === next) return next;
  const byId = new Map(previous.agents.map((a) => [a.id, a]));
  const agents = next.agents.map((a) => shareAgent(byId.get(a.id), a));
  const rootEntries = shareEntries(previous.rootEntries, next.rootEntries) as LogEntry[];
  const resultEntries = shareEntries(previous.resultEntries, next.resultEntries) as LogEntry[];
  const sameAgents = agents.length === previous.agents.length && agents.every((a, i) => a === previous.agents[i]);
  const same = sameAgents && rootEntries === previous.rootEntries && resultEntries === previous.resultEntries && previous.status === next.status
    && previous.input === next.input && previous.finalOutput === next.finalOutput && previous.provider === next.provider && previous.nodeModelLabel === next.nodeModelLabel
    && JSON.stringify(previous.usage) === JSON.stringify(next.usage) && JSON.stringify(previous.responseRecords) === JSON.stringify(next.responseRecords);
  return same ? previous : { ...next, agents: sameAgents ? previous.agents : agents, rootEntries, resultEntries };
}

/** Every run shared with the run of the same id last seen. */
export function shareRuns(previous: readonly MightyGraphRun[], next: readonly MightyGraphRun[]): readonly MightyGraphRun[] {
  if (previous === next) return next;
  const byId = new Map(previous.map((r) => [r.id, r]));
  const out = next.map((r) => shareRun(byId.get(r.id), r));
  return out.length === previous.length && out.every((r, i) => r === previous[i]) ? previous : out;
}
