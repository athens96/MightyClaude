// The shared parity vectors (native/contracts/graph-vectors.json, read where they live): the layout and camera groups the
// page computes itself. Numbers compare within 0.001, as GraphVectorVerification.cs does.
import { describe, expect, it } from 'vitest';
import vectors from '@contracts/graph-vectors.json';
import type { MightyGraphRun } from '../store/models';
import { makeLayout, originXOf, canvasWidth, type Rect } from './layout';
import { admittedCamera, cameraOffset, lostFrameIndex, reaimAnchor, resizeAnchor, trimAnchor, trimToken, type Anchor } from './camera';

type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

function difference(expected: Json, produced: unknown, path: string): string | null {
  if (expected === null) return produced === null || produced === undefined ? null : `${path}: expected null, produced ${JSON.stringify(produced)}`;
  if (typeof expected === 'number') return typeof produced === 'number' && Math.abs(produced - expected) <= 0.001 ? null : `${path}: expected ${expected}, produced ${JSON.stringify(produced)}`;
  if (typeof expected !== 'object') return expected === produced ? null : `${path}: expected ${JSON.stringify(expected)}, produced ${JSON.stringify(produced)}`;
  if (Array.isArray(expected)) {
    if (!Array.isArray(produced)) return `${path}: expected an array`;
    if (produced.length !== expected.length) return `${path}: expected ${expected.length} items, produced ${produced.length}`;
    for (let i = 0; i < expected.length; i++) { const d = difference(expected[i]!, produced[i], `${path}[${i}]`); if (d) return d; }
    return null;
  }
  if (typeof produced !== 'object' || produced === null) return `${path}: expected an object`;
  const keys = Object.keys(expected).sort();
  const got = Object.keys(produced).sort();
  if (keys.join() !== got.join()) return `${path}: expected keys [${keys}], produced [${got}]`;
  for (const key of keys) { const d = difference(expected[key]!, (produced as Record<string, unknown>)[key], `${path}.${key}`); if (d) return d; }
  return null;
}

/** The vectors write absent optional fields as null; the wire omits them. */
function run(value: Record<string, unknown>): MightyGraphRun {
  const strip = (o: Record<string, unknown>) => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== null));
  const r = strip(value) as unknown as MightyGraphRun;
  return { ...r, agents: (r.agents ?? []).map((a) => strip(a as unknown as Record<string, unknown>) as unknown as MightyGraphRun['agents'][number]), rootEntries: r.rootEntries ?? [], resultEntries: r.resultEntries ?? [] };
}

const anchorJson = (a: Anchor) => (a.kind === 'hold' ? { kind: 'hold' } : { kind: 'reaim', nodeID: a.nodeID, alignTop: a.alignTop });

interface Case { name: string; expected: Json; [key: string]: unknown }
const groups = vectors as unknown as Record<string, Case[]>;

describe('graph vectors: layout', () => {
  for (const c of groups.layout!) {
    it(c.name, () => {
      const viewport = c.viewport as { w: number; h: number } | undefined;
      const layout = makeLayout(((c.runs as Record<string, unknown>[]) ?? []).map(run), (c.draft as string) ?? '', (c.running as boolean) ?? false,
        new Set((c.expanded as string[]) ?? []), {
          resultFilesRunID: (c.resultFilesRunID as string) ?? null,
          viewport: viewport ?? null,
          planRunID: (c.planRunID as string) ?? null,
          planRecords: ((c.planRecords as { runID: string; recordID: string }[]) ?? []),
        });
      const produced = {
        nodes: layout.nodes.map((n) => ({ id: n.id, kind: n.kind, frame: n.frame })),
        edges: layout.edges,
        size: layout.size,
        originX: layout.originX,
        fittedResultID: layout.fittedResultID,
      };
      expect(difference(c.expected, produced, c.name)).toBeNull();
    });
  }
});

describe('graph vectors: camera', () => {
  for (const c of groups.camera!) {
    it(c.name, () => {
      const a = c.args as Record<string, Json>;
      const point = (k: string) => (a[k] && typeof a[k] === 'object' ? (a[k] as { x: number; y: number }) : null);
      const size = (k: string) => (a[k] && typeof a[k] === 'object' ? (a[k] as { w: number; h: number }) : { w: 0, h: 0 });
      const rects = (k: string) => ((a[k] as unknown as Rect[]) ?? []);
      const text = (k: string) => (typeof a[k] === 'string' ? (a[k] as string) : null);
      let produced: unknown;
      switch (c.fn) {
        case 'trimAnchor': produced = anchorJson(trimAnchor((a.previousRunIDs as string[]) ?? [], (a.runIDs as string[]) ?? [], text('selectedNodeID'), new Set((a.layoutNodeIDs as string[]) ?? []))); break;
        case 'reaimAnchor': produced = anchorJson(reaimAnchor(text('newestRunID'), text('selectedNodeID'), new Set((a.layoutNodeIDs as string[]) ?? []))); break;
        case 'resizeAnchor': produced = anchorJson(resizeAnchor(text('fittedResultID'), text('targetID'), a.targetAlignTop === true, (a.frames as unknown as Record<string, Rect>) ?? {})); break;
        case 'cameraOffset': produced = cameraOffset(a.frame as unknown as Rect, size('viewport'), (a.zoom as number) ?? 1, a.alignTop === true); break;
        case 'lostFrameIndex': { const index = lostFrameIndex(rects('previousFrames'), rects('currentFrames'), point('camera') ?? { x: 0, y: 0 }, size('viewport'), (a.zoom as number) ?? 1); produced = { index, stranded: index !== null }; break; }
        case 'admittedCamera': produced = admittedCamera(text('targetToken'), text('consumedToken'), point('targetCamera'), point('current') ?? { x: 0, y: 0 }, point('requested') ?? { x: 0, y: 0 }); break;
        case 'trimToken': produced = trimToken(a.sequence as number, text('nodeID') ?? ''); break;
        case 'originX': produced = { originX: originXOf((a.leadingMinX as number) ?? 0), canvasWidth: canvasWidth((a.leadingMinX as number) ?? 0, (a.trailingMaxX as number) ?? 0) }; break;
        default: throw new Error('unknown camera fn ' + String(c.fn));
      }
      expect(difference(c.expected, produced, c.name)).toBeNull();
    });
  }
});
