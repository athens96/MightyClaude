// A pane's remembered block sizes, ported from Core/GraphBlockPreferences.cs. The host saves them (its own copy of these
// rules, through `mightySetBlockSize`); the page applies the same rules at once so a released drag does not snap back
// while the delta is on its way.
import type { GraphBlockSize, MightyGraphRun } from '../store/models';
import { PendingNodeID, PlanRecordSuffix, WorkSuffix, nodeId, normalizedSizeFor, suffixOf, terminal } from './layout';

export const MaximumSizes = 1024;
export const SplitLayout = 1;

const control = /[\u0000-\u001f\u007f-\u009f]/;
const clean = (id: string, size: GraphBlockSize | undefined) =>
  id.length > 0 && id.length <= 512 && !control.test(id) ? normalizedSizeFor(size, id) : null;

export function normalizeAll(values: Readonly<Record<string, GraphBlockSize>> | null | undefined): Record<string, GraphBlockSize> | null {
  if (!values) return null;
  const result: Record<string, GraphBlockSize> = {};
  let n = 0;
  for (const [id, size] of Object.entries(values)) {
    if (n++ >= 2 * MaximumSizes) break;
    const c = clean(id, size);
    if (c) result[id] = c;
  }
  return Object.keys(result).length === 0 ? null : result;
}

const settled = (run: MightyGraphRun) => terminal(run.status) && run.agents.every((a) => terminal(a.status));

export function normalizeForRuns(values: Readonly<Record<string, GraphBlockSize>> | null | undefined, runs: readonly MightyGraphRun[]): Record<string, GraphBlockSize> | null {
  if (!values || Object.keys(values).length === 0) return null;
  const result: Record<string, GraphBlockSize> = {};
  let places = 0;
  const retain = (...ids: string[]) => {
    if (places >= MaximumSizes) return;
    let kept = false;
    for (const id of ids) {
      const size = clean(id, values[id]);
      if (size) { result[id] = size; kept = true; }
    }
    if (kept) places++;
  };
  retain(PendingNodeID);
  for (let i = runs.length - 1; i >= 0; i--) {
    const run = runs[i]!;
    retain(nodeId(run.id, 'request'), nodeId(run.id, WorkSuffix));
    if (settled(run)) retain(nodeId(run.id, 'result'));
    for (const agent of run.agents) retain(nodeId(run.id, 'agent:' + agent.id));
    const records = nodeId(run.id, PlanRecordSuffix);
    for (const id of Object.keys(values).filter((key) => key.startsWith(records)).sort(ordinal)) retain(id);
    if (places >= MaximumSizes) break;
  }
  return Object.keys(result).length === 0 ? null : result;
}

const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

export function splittingExecution(values: Readonly<Record<string, GraphBlockSize>> | null | undefined): Record<string, GraphBlockSize> | null {
  if (!values) return null;
  const result: Record<string, GraphBlockSize> = { ...values };
  for (const [id, size] of Object.entries(values)) {
    if (suffixOf(id) !== 'request') continue;
    delete result[id];
    const work = id.slice(0, id.length - 'request'.length) + WorkSuffix;
    if (!(work in result)) result[work] = size;
  }
  return Object.keys(result).length === 0 ? null : result;
}

/** GraphBlockPreferences.Set over a pane's saved sizes. */
export function setBlockSize(sizes: Readonly<Record<string, GraphBlockSize>> | null | undefined, id: string, size: GraphBlockSize | null): Record<string, GraphBlockSize> | null {
  const next = normalizeAll(sizes) ?? {};
  delete next[id];
  const normalized = normalizedSizeFor(size, id);
  if (normalized) {
    const keys = Object.keys(next);
    if (keys.length >= 2 * MaximumSizes) delete next[keys[0]!];
    next[id] = normalized;
  }
  return normalizeAll(next);
}

