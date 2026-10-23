// The Mighty diagram's layout, ported from Core/MightyGraphLayout.cs (itself the macOS MightyGraphLayout): pure, checked
// against native/contracts/graph-vectors.json (splitsExecution off) and the Windows rules (splitsExecution on).
import type { GraphBlockSize, MightyGraphRun } from '../store/models';

export interface Rect { x: number; y: number; w: number; h: number }
export interface Size { w: number; h: number }
export interface LayoutNode { id: string; kind: string; frame: Rect }
export interface LayoutEdge { source: string; target: string; joins: boolean }

export interface Layout {
  nodes: LayoutNode[];
  edges: LayoutEdge[];
  size: Size;
  originX: number;
  originY: number;
  fittedResultID: string | null;
  viewportBoundResultID: string | null;
  resultLimit: Size | null;
  resultWindowFit: Size | null;
  planLimit: Size | null;
  planWindowFit: Size | null;
}

// ── constants (MightyGraphCamera.cs, MightyGraph.cs MightyGraphBlockSize, MightyGraphLayout.cs) ──
export const RequestWidth = 500;
export const Margin = 24;
export const CentreX = Margin + RequestWidth / 2;
export const PendingNodeID = 'pending-input';
export const HistoryNodeID = 'history-top';
export const HistoryWidth = 380;
export const HistoryHeight = 44;
export const PlanSuffix = 'plan';
export const PlanWidth = 640;
export const PlanHeight = 520;
export const PlanRecordSuffix = 'plan-record:';
export const PlanRecordWidth = 320;
export const WorkSuffix = 'execution';
export const WorkKind = 'work';
export const ExecutionKind = 'execution';
export const ExecutionSuffix = 'ouroboros-execution:';
export const ExecutionWidth = 380;
export const ImagesKind = 'images';
export const ImagesSuffix = 'images:';
export const ImagesWidth = 304;
export const ImagesColumns = 3;
export const ImagesTile = 88;
export const RequestStep = 'request';
export const MinimumWidth = 300;
export const MinimumHeight = 140;
export const MaximumWidth = 1400;
export const MaximumHeight = 1200;
const SiblingGap = 20;
const RowGap = 32;
const AttachmentGap = 8;

export const planRecordHeight = (expanded: boolean) => (expanded ? 420 : 104);
export const executionHeight = (expanded: boolean) => (expanded ? 470 : 290);
export const requestHeight = (expanded: boolean) => (expanded ? 280 : 140);
export const visibleImages = (count: number, expanded: boolean) => Math.min(count, ImagesColumns * (expanded ? 4 : 2));
export function imagesHeight(count: number, expanded: boolean): number {
  const rows = Math.max(1, Math.floor((visibleImages(count, expanded) + ImagesColumns - 1) / ImagesColumns));
  return 38 + 24 + rows * ImagesTile + (rows - 1) * 8;
}

export const cameraX = (treeWidth: number) => CentreX - treeWidth / 2;
export const originXOf = (leadingMinX: number) => Math.min(0, leadingMinX - Margin);
export const canvasWidth = (leading: number, trailing: number) => Math.max(trailing + Margin, RequestWidth + Margin * 2) - originXOf(leading);

export const maxX = (r: Rect) => r.x + r.w;
export const maxY = (r: Rect) => r.y + r.h;

// ── node ids ──
const encoder = new TextEncoder();
const decoder = new TextDecoder();
export const nodeId = (runId: string, suffix: string) => `${encoder.encode(runId).length}:${runId}:${suffix}`;

/** The suffix of a {@link nodeId}, read past its length-prefixed run id (UTF-8 bytes). */
export function suffixOf(id: string): string | null {
  const colon = id.indexOf(':');
  if (colon <= 0 || !/^\d+$/.test(id.slice(0, colon))) return null;
  const count = Number(id.slice(0, colon));
  const bytes = encoder.encode(id);
  const separator = colon + 1 + count;
  if (separator >= bytes.length || bytes[separator] !== 0x3a) return null;
  return decoder.decode(bytes.slice(separator + 1));
}

/** The run id of a {@link nodeId}. */
export function runOf(id: string): string | null {
  const colon = id.indexOf(':');
  if (colon <= 0 || !/^\d+$/.test(id.slice(0, colon))) return null;
  const bytes = encoder.encode(id);
  const count = Number(id.slice(0, colon));
  return decoder.decode(bytes.slice(colon + 1, colon + 1 + count));
}

export const minimumHeightFor = (id: string) => (suffixOf(id)?.startsWith(PlanRecordSuffix) ? planRecordHeight(false) : MinimumHeight);

function normalizedAtLeast(size: GraphBlockSize | null | undefined, minimumHeight: number): GraphBlockSize | null {
  if (!size || !Number.isFinite(size.width) || !Number.isFinite(size.height)) return null;
  return {
    width: Math.min(MaximumWidth, Math.max(MinimumWidth, size.width)),
    height: Math.min(MaximumHeight, Math.max(minimumHeight, size.height)),
  };
}
export const normalizedSize = (size: GraphBlockSize | null | undefined) => normalizedAtLeast(size, MinimumHeight);
export const normalizedSizeFor = (size: GraphBlockSize | null | undefined, id: string) => normalizedAtLeast(size, minimumHeightFor(id));

export function minimumBlockSize(kind: string): Size {
  if (kind === 'planRecord') return { w: MinimumWidth, h: planRecordHeight(false) };
  return { w: MinimumWidth, h: MinimumHeight };
}

export const isAuxiliaryKind = (kind: string) => kind === 'resultFiles' || kind === 'planRecord' || kind === ExecutionKind || kind === ImagesKind;
export const isResizable = (kind: string) =>
  kind === 'request' || kind === WorkKind || kind === 'agent' || kind === 'result' || kind === 'draft' || kind === 'plan' || kind === 'planRecord';

export const terminal = (state: string) =>
  state === 'completed' || state === 'error' || state === 'failed' || state === 'stopped' || state === 'cancelled' || state === 'interrupted';
export const finished = (run: MightyGraphRun) => terminal(run.status) && run.agents.every((a) => terminal(a.status));
export const runNodeId = (run: MightyGraphRun, suffix: string) => nodeId(run.id, suffix);

export function nodeAt(layout: Layout, x: number, y: number): LayoutNode | undefined {
  return layout.nodes.find((n) => x >= n.frame.x && x < maxX(n.frame) && y >= n.frame.y && y < maxY(n.frame));
}

export function newestRequestTargetID(layout: Layout, runs: readonly MightyGraphRun[]): string | null {
  const last = runs[runs.length - 1];
  if (!last) return null;
  const work = runNodeId(last, WorkSuffix);
  return !finished(last) && layout.nodes.some((n) => n.id === work) ? work : runNodeId(last, 'request');
}

export function latestResultID(runs: readonly MightyGraphRun[]): string | null {
  for (let i = runs.length - 1; i >= 0; i--) if (finished(runs[i] as MightyGraphRun)) return runNodeId(runs[i] as MightyGraphRun, 'result');
  return null;
}

// ── the newest result card within the pane ──
export const MinimumResultHeight = MinimumHeight;

export const resultFitSize = (viewport: Size, filesPanelOpen: boolean): Size => ({
  w: Math.max(500, viewport.w - 48 - (filesPanelOpen ? 336 : 0)),
  h: Math.max(200, viewport.h - 48),
});

export function resultViewportLimit(viewport: Size, zoom: number, filesPanelOpen: boolean): Size {
  const scale = Number.isFinite(zoom) && zoom > 0 ? zoom : 1;
  return {
    w: Math.max(MinimumWidth, (viewport.w - 48) / scale - (filesPanelOpen ? 336 : 0)),
    h: Math.max(MinimumResultHeight, (viewport.h - 48) / scale),
  };
}

export const resultCap = (maximum: Size, limit: Size | null): Size =>
  limit ? { w: Math.min(maximum.w, limit.w), h: Math.min(maximum.h, limit.h) } : maximum;

export function resultSize(cap: Size, contentHeight: number | null | undefined): Size {
  if (contentHeight === null || contentHeight === undefined || !Number.isFinite(contentHeight) || contentHeight < 0) return cap;
  return { w: cap.w, h: Math.min(cap.h, Math.max(MinimumResultHeight, Math.ceil(contentHeight))) };
}

export function planSize(saved: GraphBlockSize | null | undefined, viewport: Size | null, zoom: number | null): { size: Size; limit: Size | null; windowFit: Size | null } {
  const n = normalizedSize(saved);
  const savedSize = n ? { w: n.width, h: n.height } : null;
  if (!viewport) return { size: savedSize ?? { w: PlanWidth, h: PlanHeight }, limit: null, windowFit: null };
  const fit = resultFitSize(viewport, false);
  const limit = zoom !== null ? resultViewportLimit(viewport, zoom, false) : null;
  return { size: resultCap(savedSize ?? fit, limit), limit, windowFit: fit };
}

export const resultDragSides = (start: Size, dragged: Size) => ({ horizontal: Math.abs(dragged.w - start.w) > 0.5, vertical: Math.abs(dragged.h - start.h) > 0.5 });

export function filesPanelOpen(runs: readonly MightyGraphRun[], resultFilesRunID: string | null): boolean {
  for (let i = runs.length - 1; i >= 0; i--) {
    const run = runs[i] as MightyGraphRun;
    if (finished(run)) return resultFilesRunID !== null && run.id === resultFilesRunID && run.status === 'completed';
  }
  return false;
}

export type ResizePhase = 'live' | 'finished' | 'cancelled';

export function resultDrag(
  dragged: Size, horizontal: boolean, vertical: boolean, phase: ResizePhase,
  saved: GraphBlockSize | null, limit: Size | null, windowFit: Size | null,
): { live: GraphBlockSize | null; save: GraphBlockSize | null } {
  if (phase === 'cancelled') return { live: null, save: null };
  if (phase === 'live') {
    const shown = resultCap(dragged, limit);
    return { live: normalizedSize({ width: shown.w, height: shown.h }), save: null };
  }
  const side = (moved: boolean, value: number, savedSide: number | undefined, fit: number | undefined, lim: number | undefined) => {
    const maximum = savedSide ?? fit;
    if (!moved) return maximum ?? value;
    if (lim === undefined || value < lim - 0.5) return value;
    return Math.max(maximum ?? lim, lim);
  };
  const width = side(horizontal, dragged.w, saved?.width, windowFit?.w, limit?.w);
  const height = side(vertical, dragged.h, saved?.height, windowFit?.h, limit?.h);
  return { live: null, save: normalizedSize({ width, height }) };
}

// ── Make ──
interface Tree { nodes: LayoutNode[]; edges: LayoutEdge[]; width: number; height: number; top: string; leaves: string[] }

function offset(tree: Tree, dx: number, dy: number): void {
  tree.nodes = tree.nodes.map((n) => ({ ...n, frame: { ...n.frame, x: n.frame.x + dx, y: n.frame.y + dy } }));
}

export interface MakeOptions {
  resultFilesRunID?: string | null;
  viewport?: Size | null;
  hasSharedResultSize?: boolean;
  zoom?: number | null;
  sharedResultSize?: GraphBlockSize | null;
  retainedStart?: number;
  history?: boolean;
  resultContentHeight?: number | null;
  blockSizes?: Readonly<Record<string, GraphBlockSize>> | null;
  planRunID?: string | null;
  planRecords?: readonly { runID: string; recordID: string }[];
  planSize?: GraphBlockSize | null;
  splitsExecution?: boolean;
  galleries?: readonly { runID: string; step: string; count: number }[];
  executions?: readonly { runID: string; key: string }[];
}

export function makeLayout(runs: readonly MightyGraphRun[], draft: string, running: boolean, expanded: ReadonlySet<string>, options: MakeOptions = {}): Layout {
  const viewport = options.viewport ?? null;
  const zoom = options.zoom ?? null;
  const splitsExecution = options.splitsExecution ?? false;
  const resultFilesRunID = options.resultFilesRunID ?? null;
  const latest = latestResultID(runs);
  const filesOpen = filesPanelOpen(runs, resultFilesRunID);
  const sharedNorm = normalizedSize(options.sharedResultSize);
  const shared = sharedNorm ? { w: sharedNorm.width, h: sharedNorm.height } : null;
  const hasShared = (options.hasSharedResultSize ?? false) || shared !== null;
  const autoFit = viewport && latest !== null ? resultFitSize(viewport, filesOpen) : null;
  const resultLimit = viewport && zoom !== null && latest !== null ? resultViewportLimit(viewport, zoom, filesOpen) : null;
  const resultMaximum = shared ?? (hasShared ? null : autoFit);

  const sizeOf = (id: string, w: number, h: number): Size => {
    if (latest !== null && id === latest && viewport) {
      if (resultMaximum) return resultSize(resultCap(resultMaximum, resultLimit), options.resultContentHeight);
      if (hasShared) return { w, h };
    }
    const custom = normalizedSizeFor(options.blockSizes?.[id], id);
    return custom ? { w: custom.width, h: custom.height } : { w, h };
  };

  const trees: Tree[] = [];
  let planFit: ReturnType<typeof planSize> | null = null;
  for (const run of runs) {
    const mainID = runNodeId(run, 'request');
    const mainSize = sizeOf(mainID, RequestWidth, splitsExecution ? requestHeight(expanded.has(mainID)) : expanded.has(mainID) ? 540 : 280);
    const mainHeight = mainSize.h;
    const workID = runNodeId(run, WorkSuffix);
    const workSize = splitsExecution ? sizeOf(workID, RequestWidth, expanded.has(workID) ? 540 : 280) : null;
    const resultID = runNodeId(run, 'result');
    const resSize = sizeOf(resultID, RequestWidth, expanded.has(resultID) ? 440 : 200);
    const runFinished = finished(run);

    const visited = new Set<number>();
    const indexes = new Map<string, number>();
    run.agents.forEach((a, i) => { if (!indexes.has(a.id)) indexes.set(a.id, i); });

    const branch = (index: number, depth = 0): Tree | null => {
      if (depth >= 64 || visited.has(index)) return null;
      visited.add(index);
      const agent = run.agents[index]!;
      const id = runNodeId(run, 'agent:' + agent.id);
      const card = sizeOf(id, 360, expanded.has(id) ? 480 : 240);
      const height = card.h;
      const children: Tree[] = [];
      for (let ci = 0; ci < run.agents.length; ci++) {
        if (ci !== index && run.agents[ci]!.parentID === agent.id) {
          const child = branch(ci, depth + 1);
          if (child) children.push(child);
        }
      }
      const childrenWidth = children.reduce((s, c) => s + c.width, 0) + Math.max(0, children.length - 1) * SiblingGap;
      const width = Math.max(card.w, childrenWidth);
      const result: Tree = { nodes: [{ id, kind: 'agent', frame: { x: (width - card.w) / 2, y: 0, w: card.w, h: height } }], edges: [], width, height, top: id, leaves: [id] };
      if (children.length > 0) {
        result.leaves = [];
        let x = (width - childrenWidth) / 2;
        for (const child of children) {
          offset(child, x, height + RowGap);
          result.nodes.push(...child.nodes);
          result.edges.push({ source: id, target: child.top, joins: false });
          result.edges.push(...child.edges);
          result.leaves.push(...child.leaves);
          result.height = Math.max(result.height, height + RowGap + child.height);
          x += child.width + SiblingGap;
        }
      }
      return result;
    };

    const branches: Tree[] = [];
    run.agents.forEach((a, ai) => {
      const parent = a.parentID;
      if (parent === undefined || parent === null || !indexes.has(parent) || parent === a.id) {
        const t = branch(ai);
        if (t) branches.push(t);
      }
    });
    run.agents.forEach((_, ai) => {
      if (!visited.has(ai)) { const t = branch(ai); if (t) branches.push(t); }
    });

    const branchWidth = branches.reduce((s, b) => s + b.width, 0) + Math.max(0, branches.length - 1) * SiblingGap;
    const treeWidth = Math.max(Math.max(mainSize.w, workSize?.w ?? 0), Math.max(branchWidth, runFinished ? resSize.w : 0));
    const tree: Tree = {
      nodes: [{ id: mainID, kind: 'request', frame: { x: (treeWidth - mainSize.w) / 2, y: 0, w: mainSize.w, h: mainHeight } }],
      edges: [], width: treeWidth, height: mainHeight, top: mainID, leaves: [mainID],
    };
    let parentID = mainID;
    if (workSize) {
      tree.nodes.push({ id: workID, kind: WorkKind, frame: { x: (treeWidth - workSize.w) / 2, y: mainHeight + RowGap, w: workSize.w, h: workSize.h } });
      tree.edges.push({ source: mainID, target: workID, joins: false });
      tree.leaves = [workID];
      tree.height += RowGap + workSize.h;
      parentID = workID;
    }
    if (branches.length > 0) {
      tree.leaves = [];
      const top = tree.height + RowGap;
      let x = (treeWidth - branchWidth) / 2;
      for (const b of branches) {
        offset(b, x, top);
        tree.nodes.push(...b.nodes);
        tree.edges.push({ source: parentID, target: b.top, joins: false });
        tree.edges.push(...b.edges);
        tree.leaves.push(...b.leaves);
        tree.height = Math.max(tree.height, top + b.height);
        x += b.width + SiblingGap;
      }
    }
    if (run.id === options.planRunID && !runFinished) {
      const planID = runNodeId(run, PlanSuffix);
      const fit = planSize(options.planSize, viewport, zoom);
      planFit = fit;
      const block = fit.size;
      if (block.w > tree.width) {
        offset(tree, (block.w - tree.width) / 2, 0);
        tree.width = block.w;
      }
      tree.nodes.push({ id: planID, kind: 'plan', frame: { x: (tree.width - block.w) / 2, y: tree.height + RowGap, w: block.w, h: block.h } });
      tree.edges.push(...tree.leaves.map((l) => ({ source: l, target: planID, joins: true })));
      tree.leaves = [planID];
      tree.height += RowGap + block.h;
    }
    if (runFinished) {
      tree.nodes.push({ id: resultID, kind: 'result', frame: { x: (treeWidth - resSize.w) / 2, y: tree.height + RowGap, w: resSize.w, h: resSize.h } });
      tree.edges.push(...tree.leaves.map((l) => ({ source: l, target: resultID, joins: true })));
      tree.leaves = [resultID];
      tree.height += RowGap + resSize.h;
    }
    trees.push(tree);
  }

  const last = runs[runs.length - 1];
  const hasPending = (!running && (runs.length === 0 || (last !== undefined && finished(last)))) || draft.length > 0;
  if (hasPending) {
    const pending = sizeOf(PendingNodeID, RequestWidth, expanded.has(PendingNodeID) ? 280 : 140);
    trees.push({ nodes: [{ id: PendingNodeID, kind: 'draft', frame: { x: 0, y: 0, w: pending.w, h: pending.h } }], edges: [], width: pending.w, height: pending.h, top: PendingNodeID, leaves: [PendingNodeID] });
  }

  const layout: Layout = {
    nodes: [], edges: [], size: { w: 0, h: 0 }, originX: 0, originY: 0, fittedResultID: null, viewportBoundResultID: null,
    resultLimit: null, resultWindowFit: null, planLimit: null, planWindowFit: null,
  };
  const split = Math.min(Math.max(0, options.retainedStart ?? 0), runs.length);
  const tops = new Array<number>(trees.length).fill(0);
  let y = 24;
  for (let i = split; i < trees.length; i++) { tops[i] = y; y += trees[i]!.height + RowGap; }
  let above = 24;
  for (let i = split - 1; i >= 0; i--) { above -= trees[i]!.height + RowGap; tops[i] = above; }
  let previous: string[] = [];
  trees.forEach((tree, i) => {
    offset(tree, cameraX(tree.width), tops[i]!);
    layout.nodes.push(...tree.nodes);
    layout.edges.push(...previous.map((p) => ({ source: p, target: tree.top, joins: true })));
    layout.edges.push(...tree.edges);
    previous = tree.leaves;
  });
  const bottom = split < trees.length ? y - RowGap : 24 - RowGap;
  if (options.history) {
    const top = layout.nodes.length > 0 ? Math.min(...layout.nodes.map((n) => n.frame.y)) : 24;
    layout.nodes.unshift({ id: HistoryNodeID, kind: 'history', frame: { x: CentreX - HistoryWidth / 2, y: top - 28 - HistoryHeight, w: HistoryWidth, h: HistoryHeight } });
  }

  const any = layout.nodes.length > 0;
  const leading = any ? Math.min(...layout.nodes.map((n) => n.frame.x)) : cameraX(RequestWidth);
  const trailing = any ? Math.max(...layout.nodes.map((n) => maxX(n.frame))) : CentreX + RequestWidth / 2;
  layout.originX = originXOf(leading);
  layout.originY = Math.min(0, (any ? Math.min(...layout.nodes.map((n) => n.frame.y)) : 24) - 24);
  layout.size = {
    w: canvasWidth(leading, trailing),
    h: Math.max(188, Math.max(bottom, any ? Math.max(...layout.nodes.map((n) => maxY(n.frame))) : 0) + 24 - layout.originY),
  };
  const grow = (frame: Rect) => {
    layout.size = { w: Math.max(layout.size.w, maxX(frame) + 24 - layout.originX), h: Math.max(layout.size.h, maxY(frame) + 24 - layout.originY) };
  };

  if (resultFilesRunID !== null) {
    const panelRun = runs.find((r) => r.id === resultFilesRunID);
    if (panelRun && panelRun.status === 'completed' && finished(panelRun)) {
      const resultNode = layout.nodes.find((n) => n.kind === 'result' && n.id === runNodeId(panelRun, 'result'));
      if (resultNode) {
        const frame = { x: maxX(resultNode.frame) + 16, y: resultNode.frame.y, w: 320, h: resultNode.frame.h };
        layout.nodes.push({ id: runNodeId(panelRun, 'result-files'), kind: 'resultFiles', frame });
        layout.size = { w: Math.max(layout.size.w, maxX(frame) + 24 - layout.originX), h: layout.size.h };
      }
    }
  }
  const runIndex = (id: string) => runs.findIndex((r) => r.id === id);
  const rightOf = (top: number, height: number, owner: Rect) => {
    let right = maxX(owner);
    let found = false;
    for (const n of layout.nodes) {
      if (n.frame.y < top + height && maxY(n.frame) > top) {
        right = found ? Math.max(right, maxX(n.frame)) : maxX(n.frame);
        found = true;
      }
    }
    return Math.max(found ? right : maxX(owner), maxX(owner)) + AttachmentGap;
  };
  for (const gallery of options.galleries ?? []) {
    const index = runIndex(gallery.runID);
    if (gallery.count <= 0 || index < 0) continue;
    const run = runs[index]!;
    const stepID = runNodeId(run, splitsExecution && gallery.step === RequestStep ? WorkSuffix : gallery.step);
    const step = layout.nodes.find((n) => n.id === stepID && !isAuxiliaryKind(n.kind));
    if (!step) continue;
    const id = runNodeId(run, ImagesSuffix + gallery.step);
    if (layout.nodes.some((n) => n.id === id)) continue;
    const height = imagesHeight(gallery.count, expanded.has(id));
    const frame = { x: rightOf(step.frame.y, height, step.frame), y: step.frame.y, w: ImagesWidth, h: height };
    layout.nodes.push({ id, kind: ImagesKind, frame });
    grow(frame);
  }
  const nextY = new Map<number, number>();
  const ownerOf = (index: number) => {
    const run = runs[index]!;
    return layout.nodes.find((n) => (splitsExecution ? n.kind === WorkKind && n.id === runNodeId(run, WorkSuffix) : n.kind === 'request' && n.id === runNodeId(run, 'request')));
  };
  for (const execution of options.executions ?? []) {
    const index = runIndex(execution.runID);
    if (index < 0) continue;
    const owner = ownerOf(index);
    if (!owner) continue;
    const id = runNodeId(runs[index]!, ExecutionSuffix + execution.key);
    if (layout.nodes.some((n) => n.id === id)) continue;
    const top = nextY.get(index) ?? owner.frame.y;
    const height = executionHeight(expanded.has(id));
    const frame = { x: rightOf(top, height, owner.frame), y: top, w: ExecutionWidth, h: height };
    layout.nodes.push({ id, kind: ExecutionKind, frame });
    grow(frame);
    nextY.set(index, maxY(frame) + AttachmentGap);
  }
  for (const { runID, recordID } of options.planRecords ?? []) {
    const index = runIndex(runID);
    if (index < 0) continue;
    const request = ownerOf(index);
    if (!request) continue;
    const id = runNodeId(runs[index]!, PlanRecordSuffix + recordID);
    if (layout.nodes.some((n) => n.id === id)) continue;
    const top = nextY.get(index) ?? request.frame.y;
    const record = sizeOf(id, PlanRecordWidth, planRecordHeight(expanded.has(id)));
    const frame = { x: rightOf(top, record.h, request.frame), y: top, w: record.w, h: record.h };
    layout.nodes.push({ id, kind: 'planRecord', frame });
    grow(frame);
    nextY.set(index, maxY(frame) + AttachmentGap);
  }
  layout.fittedResultID = viewport && !hasShared ? latest : null;
  layout.resultLimit = resultLimit;
  layout.resultWindowFit = autoFit;
  layout.planLimit = planFit?.limit ?? null;
  layout.planWindowFit = planFit?.windowFit ?? null;
  if (viewport && resultMaximum) {
    const capped = resultCap(resultMaximum, resultLimit);
    if (capped.w !== resultMaximum.w || capped.h !== resultMaximum.h) layout.viewportBoundResultID = latest;
  }
  return layout;
}
