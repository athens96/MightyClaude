// Each diagram block's own parts (BlockExtra), kept from the previous pass wherever they are the same objects, so a block
// memoized on them draws again only when what it shows changed. Pure.
import type { PlanRecord, GraphBlockSize, MightyGraphRun } from '../store/models';
import type { BlockExtra } from './Block';
import { ImagesSuffix, PlanRecordSuffix, runOf, suffixOf, type Layout } from './layout';
import type { MightyInputs } from './types';

export function sameExtra(a: BlockExtra, b: BlockExtra): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]) as Set<keyof BlockExtra>;
  for (const key of keys) if (a[key] !== b[key]) return false;
  return true;
}

export interface ExtrasInput {
  layout: Layout;
  runs: readonly MightyGraphRun[];
  inputs: MightyInputs | null;
  draft: string;
  planHistory: readonly PlanRecord[] | undefined;
  resultSize: GraphBlockSize | undefined;
  planText: string | undefined;
  latest: string | null;
}

export function blockExtras({ layout, runs, inputs, draft, planHistory, resultSize, planText, latest }: ExtrasInput, previous: ReadonlyMap<string, BlockExtra>): Map<string, BlockExtra> {
  const byRun = new Map(runs.map((r) => [r.id, r]));
  const next = new Map<string, BlockExtra>();
  for (const node of layout.nodes) {
    const run = byRun.get(runOf(node.id) ?? '');
    const suffix = suffixOf(node.id) ?? '';
    let extra: BlockExtra = {};
    switch (node.kind) {
      case 'request': extra = { input: run?.input }; break;
      case 'work': extra = { entries: run?.rootEntries }; break;
      case 'agent': { const agent = run?.agents.find((a) => 'agent:' + a.id === suffix); extra = { input: agent?.input, entries: agent?.entries }; break; }
      case 'result': {
        const files = run ? inputs?.files[run.id] : undefined;
        extra = { entries: run?.resultEntries, finalOutput: run?.finalOutput, files, filesOpen: inputs?.resultFilesRunID === run?.id, fitted: layout.fittedResultID === node.id && !resultSize, measure: node.id === latest };
        break;
      }
      case 'draft': extra = { draft: draft }; break;
      case 'plan': extra = { markdown: planText }; break;
      case 'planRecord': extra = { markdown: planHistory?.find((r) => r.id === suffix.slice(PlanRecordSuffix.length))?.plan }; break;
      case 'resultFiles': extra = { files: run ? inputs?.files[run.id] : undefined }; break;
      case 'images': { const step = suffix.slice(ImagesSuffix.length); extra = { images: run ? { runId: run.id, step, count: inputs?.galleries.find((g) => g.runID === run.id && g.step === step)?.count ?? 0 } : undefined }; break; }
      case 'execution': extra = { execution: inputs?.executionCards[node.id] }; break;
      case 'history': extra = { history: inputs?.history }; break;
    }
    const before = previous.get(node.id);
    // The host's pushed data (files, an execution's card, the history state, a gallery) arrives as new objects whenever any
    // part of the inputs changed: a part equal by value keeps the object the block was drawn with.
    if (before) {
      for (const key of ['files', 'images', 'execution', 'history'] as const) {
        if (extra[key] !== undefined && before[key] !== undefined && extra[key] !== before[key] && JSON.stringify(extra[key]) === JSON.stringify(before[key])) extra = { ...extra, [key]: before[key] };
      }
    }
    next.set(node.id, before && sameExtra(before, extra) ? before : extra);
  }
  return next;
}
