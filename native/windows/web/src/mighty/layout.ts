// The Mighty diagram's layout, ported from Core/MightyGraphLayout.cs (itself the macOS MightyGraphLayout): pure, checked
// against native/contracts/graph-vectors.json (splitsExecution off) and the Windows rules (splitsExecution on).
import { newestRequestRun, quietNotice, reopenedWithReply, shownAgents, shownRuns } from './notice';
import {
  CardHeight, CardWidth, DelegateGap, DelegateHeight, DelegateKind, DelegateRowGap, DelegateSuffix, DelegateWidth, MainKey, SideGap, StepCardHeight, StepKind,
  WideCardWidth, delegationChildren, flowAgents, flowOrder, isCompact, isPendingStep, launchedInStep, runSteps, stepSlot, stepSuffix,
} from './flow';
import type { GraphBlockSize, MightyGraphAgent, MightyGraphRun } from '../store/models';

export interface Rect { x: number; y: number; w: number; h: number }
export interface Size { w: number; h: number }
/** `compact`: a sub-agent or a background command drawn as its card (flow.ts isCompact), not as its block. */
export interface LayoutNode { id: string; kind: string; frame: Rect; compact?: boolean }
/** `side`: from a spine block's trailing edge to a block it launched, beside it (macOS MightyGraphLayout.Edge.side); present only when true. */
export interface LayoutEdge { source: string; target: string; joins: boolean; side?: true }

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
/** Neither selected nor resized: a click goes to what it holds and a wheel pans past it (Core MightyGraphLayout.IsFixed): the history block, the role
 * view's delegate line and plan column, and a card. */
export const isFixed = (node: { kind: string; compact?: boolean }) =>
  node.compact === true || node.kind === 'history' || node.kind === DelegateKind;
export const isResizable = (kind: string) =>
  kind === 'request' || kind === WorkKind || kind === 'agent' || kind === 'result' || kind === 'draft' || kind === 'plan' || kind === 'planRecord' || kind === StepKind;

export const terminal = (state: string) =>
  state === 'completed' || state === 'error' || state === 'failed' || state === 'stopped' || state === 'cancelled' || state === 'interrupted';
/** Ended with every block a view draws for it (shownAgents): background work the request started runs on in the strip. */
export const finished = (run: MightyGraphRun) => terminal(run.status) && shownAgents(run).every((a) => terminal(a.status));
/** Has a result card: every finished run but a quiet notice run, and a notice run a later turn answers in again (Core MightyGraphLayout.HasResult). */
export const hasResult = (run: MightyGraphRun) => (finished(run) && !quietNotice(run)) || reopenedWithReply(run);
export const runNodeId = (run: MightyGraphRun, suffix: string) => nodeId(run.id, suffix);

export function nodeAt(layout: Layout, x: number, y: number): LayoutNode | undefined {
  return layout.nodes.find((n) => x >= n.frame.x && x < maxX(n.frame) && y >= n.frame.y && y < maxY(n.frame));
}

export function newestRequestTargetID(layout: Layout, runs: readonly MightyGraphRun[]): string | null {
  // A notice run after it is not a request.
  const last = newestRequestRun(runs);
  if (!last) return null;
  const work = runNodeId(last, WorkSuffix);
  return !finished(last) && layout.nodes.some((n) => n.id === work) ? work : runNodeId(last, 'request');
}

export function latestResultID(runs: readonly MightyGraphRun[]): string | null {
  for (let i = runs.length - 1; i >= 0; i--) if (hasResult(runs[i] as MightyGraphRun)) return runNodeId(runs[i] as MightyGraphRun, 'result');
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
    if (hasResult(run)) return resultFilesRunID !== null && run.id === resultFilesRunID && run.status === 'completed';
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
  /** The role view of a Claude pane (Core/macOS MightyGraphLayout.FanOut): a request stands over what happened — the steps that began, each with
   * what it launched beside it, or the row under its delegate line — and its answer; until the answer is there the main session's card stands in its
   * place. A request with steps has its plan column left of it. Every block but the result is a card unless the user opened it (`opened`, node ids).
   * Absent lays the runs out as every other view does. */
  fanOut?: { opened: ReadonlySet<string> } | null;
}

export function makeLayout(given: readonly MightyGraphRun[], draft: string, running: boolean, expanded: ReadonlySet<string>, options: MakeOptions = {}): Layout {
  // The runs as views draw them: a request's background blocks show only in the notice run that reports them (Core MightyGraphLayout.Make).
  const runs = shownRuns(given);
  const viewport = options.viewport ?? null;
  const zoom = options.zoom ?? null;
  const fanOut = options.fanOut ?? null;
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
    const runResult = hasResult(run);

    const visited = new Set<number>();
    // With a fan-out the run's background sub-agents and commands are laid out too, each where it was launched (Core MightyGraphLayout.Make fanOut).
    const agents: MightyGraphAgent[] = fanOut ? flowAgents(run) : run.agents;
    const ordered = (values: number[]) => (fanOut ? [...values].sort((a, b) => flowOrder(run, a) - flowOrder(run, b)) : values);
    const indexes = new Map<string, number>();
    agents.forEach((a, i) => { if (!indexes.has(a.id)) indexes.set(a.id, i); });
    // A card counts the blocks under it instead of drawing them: they are laid out nowhere.
    const fold = (index: number, depth: number): void => {
      if (depth >= 64 || visited.has(index)) return;
      visited.add(index);
      for (let ci = 0; ci < agents.length; ci++) if (ci !== index && agents[ci]!.parentID === agents[index]!.id) fold(ci, depth + 1);
    };

    const branch = (index: number, depth = 0): Tree | null => {
      if (depth >= 64 || visited.has(index)) return null;
      visited.add(index);
      const agent = agents[index]!;
      const id = runNodeId(run, 'agent:' + agent.id);
      const compact = fanOut !== null && isCompact(agent, fanOut.opened.has(id));
      // A task card with Claude's reply to its notice is taller (M/MightyCore/MightyGraphLayout.swift).
      // A card beside a request's step names the step on one more line (Core/macOS FlowCard.stepSize).
      const card = compact ? { w: CardWidth, h: fanOut !== null && launchedInStep(agent, run, agents) ? StepCardHeight : CardHeight } : sizeOf(id, 360, expanded.has(id) ? 480 : agent.kind === 'task' && agent.noticeReply != null ? 320 : 240);
      const height = card.h;
      const below = ordered(agents.map((_, ci) => ci).filter((ci) => ci !== index && agents[ci]!.parentID === agent.id));
      if (compact) for (const ci of below) fold(ci, depth + 1);
      const children: Tree[] = [];
      if (!compact) for (const ci of below) { const child = branch(ci, depth + 1); if (child) children.push(child); }
      const childrenWidth = children.reduce((s, c) => s + c.width, 0) + Math.max(0, children.length - 1) * SiblingGap;
      // A block that delegated says so on the branch to its row (flow.ts delegationChildren).
      const delegates = fanOut !== null && children.length > 0 && delegationChildren(agent.id, run).length > 0;
      const width = Math.max(card.w, childrenWidth, delegates ? DelegateWidth : 0);
      const node: LayoutNode = { id, kind: 'agent', frame: { x: (width - card.w) / 2, y: 0, w: card.w, h: height } };
      if (compact) node.compact = true;
      const result: Tree = { nodes: [node], edges: [], width, height, top: id, leaves: [id] };
      if (children.length > 0) {
        result.leaves = [];
        let source = id;
        let top = height + RowGap;
        if (delegates) {
          const line = runNodeId(run, DelegateSuffix + agent.id);
          const frame = { x: (width - DelegateWidth) / 2, y: height + DelegateGap, w: DelegateWidth, h: DelegateHeight };
          result.nodes.push({ id: line, kind: DelegateKind, frame });
          result.edges.push({ source: id, target: line, joins: false });
          source = line;
          top = maxY(frame) + DelegateRowGap;
        }
        let x = (width - childrenWidth) / 2;
        for (const child of children) {
          offset(child, x, top);
          result.nodes.push(...child.nodes);
          result.edges.push({ source, target: child.top, joins: false });
          result.edges.push(...child.edges);
          result.leaves.push(...child.leaves);
          result.height = Math.max(result.height, top + child.height);
          x += child.width + SiblingGap;
        }
      }
      return result;
    };

    const isTop = (ai: number) => {
      const parent = agents[ai]!.parentID;
      return parent === undefined || parent === null || !indexes.has(parent) || parent === agents[ai]!.id;
    };
    // A plan waiting for its answer stands where the result will.
    const planWaits = run.id === options.planRunID && !runFinished;
    // The role view: while there is no answer yet the main session's card stands in its place (the 실행 block, a card until opened).
    const endsInWork = fanOut !== null && splitsExecution && !runResult && !planWaits;
    const workCompact = fanOut !== null && !fanOut.opened.has(workID);
    const workShown: Size | null = workCompact ? { w: WideCardWidth, h: CardHeight } : workSize;
    // The request's steps that began, by their index among all its steps; one not begun is only in the plan column.
    const started = fanOut === null ? [] : runSteps(run).map((step, index) => ({ index, step })).filter((s) => !isPendingStep(s.step));
    let treeWidth: number;
    let tree: Tree;
    if (started.length > 0) {
      // The request's spine: the request, then each step that began, every block launched from the main session beside the one whose time it was
      // launched in (before the first step, beside the request).
      const spineSteps = started.map((s) => s.step);
      const slots: Tree[][] = started.map(() => [] as Tree[]).concat([[]]);
      for (const ai of ordered(agents.map((_, i) => i))) {
        if (!isTop(ai)) continue;
        const t = branch(ai);
        if (t) slots[stepSlot(agents[ai]!, spineSteps)]!.push(t);
      }
      agents.forEach((_, ai) => { if (!visited.has(ai)) { const t = branch(ai); if (t) slots[0]!.push(t); } });
      const spine: { id: string; kind: string; size: Size; compact: boolean }[] = [{ id: mainID, kind: 'request', size: mainSize, compact: false }];
      for (const { step } of started) {
        const id = runNodeId(run, stepSuffix(step));
        const compact = fanOut !== null && !fanOut.opened.has(id);
        spine.push({ id, kind: StepKind, size: compact ? { w: WideCardWidth, h: CardHeight } : sizeOf(id, 360, expanded.has(id) ? 480 : 240), compact });
      }
      // The spine stands on the run's centreline: the column beside it is matched by room on the other side.
      let half = Math.max(runResult ? resSize.w : 0, endsInWork ? workShown?.w ?? 0 : 0) / 2;
      spine.forEach((block, slot) => {
        const column = Math.max(0, ...slots[slot]!.map((t) => t.width));
        half = Math.max(half, block.size.w / 2 + (column > 0 ? SideGap + column : 0));
      });
      treeWidth = 2 * half;
      tree = { nodes: [], edges: [], width: treeWidth, height: 0, top: mainID, leaves: [] };
      let y = 0;
      let above: string | null = null;
      spine.forEach((block, slot) => {
        const frame = { x: half - block.size.w / 2, y: above === null ? 0 : y + RowGap, w: block.size.w, h: block.size.h };
        const node: LayoutNode = { id: block.id, kind: block.kind, frame };
        if (block.compact) node.compact = true;
        tree.nodes.push(node);
        if (above !== null) tree.edges.push({ source: above, target: block.id, joins: false });
        let bottom = maxY(frame);
        let top = frame.y;
        for (const child of slots[slot]!) {
          offset(child, maxX(frame) + SideGap, top);
          tree.nodes.push(...child.nodes);
          tree.edges.push({ source: block.id, target: child.top, joins: false, side: true });
          tree.edges.push(...child.edges);
          bottom = Math.max(bottom, top + child.height);
          top += child.height + SiblingGap;
        }
        y = bottom;
        above = block.id;
      });
      tree.height = y;
      tree.leaves = [above ?? mainID];
    } else {
      const branches: Tree[] = [];
      for (const ai of ordered(agents.map((_, i) => i))) {
        if (!isTop(ai)) continue;
        const t = branch(ai);
        if (t) branches.push(t);
      }
      agents.forEach((_, ai) => {
        if (!visited.has(ai)) { const t = branch(ai); if (t) branches.push(t); }
      });
      const branchWidth = branches.reduce((s, b) => s + b.width, 0) + Math.max(0, branches.length - 1) * SiblingGap;
      // The role view draws the 실행 block only where the answer will be (below); every other view right under the request.
      const workUnder = fanOut === null ? workSize : null;
      treeWidth = Math.max(mainSize.w, workUnder?.w ?? 0, branchWidth, runResult ? resSize.w : 0, endsInWork ? workShown?.w ?? 0 : 0);
      tree = {
        nodes: [{ id: mainID, kind: 'request', frame: { x: (treeWidth - mainSize.w) / 2, y: 0, w: mainSize.w, h: mainHeight } }],
        edges: [], width: treeWidth, height: mainHeight, top: mainID, leaves: [mainID],
      };
      let parentID = mainID;
      if (workUnder) {
        tree.nodes.push({ id: workID, kind: WorkKind, frame: { x: (treeWidth - workUnder.w) / 2, y: mainHeight + RowGap, w: workUnder.w, h: workUnder.h } });
        tree.edges.push({ source: mainID, target: workID, joins: false });
        tree.leaves = [workID];
        tree.height += RowGap + workUnder.h;
        parentID = workID;
      }
      if (branches.length > 0) {
        tree.leaves = [];
        let top = tree.height + RowGap;
        // The main session's delegate line stands between it and the row.
        if (fanOut !== null && delegationChildren(MainKey, run).length > 0) {
          const line = runNodeId(run, DelegateSuffix + MainKey);
          const frame = { x: (treeWidth - DelegateWidth) / 2, y: tree.height + DelegateGap, w: DelegateWidth, h: DelegateHeight };
          tree.nodes.push({ id: line, kind: DelegateKind, frame });
          tree.edges.push({ source: parentID, target: line, joins: false });
          parentID = line;
          top = maxY(frame) + DelegateRowGap;
        }
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
    }
    if (planWaits) {
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
    // The role view: the main session at work, where its answer will come, until it is there.
    if (endsInWork && workShown) {
      const node: LayoutNode = { id: workID, kind: WorkKind, frame: { x: (treeWidth - workShown.w) / 2, y: tree.height + RowGap, w: workShown.w, h: workShown.h } };
      if (workCompact) node.compact = true;
      tree.nodes.push(node);
      tree.edges.push(...tree.leaves.map((l) => ({ source: l, target: workID, joins: true })));
      tree.leaves = [workID];
      tree.height += RowGap + workShown.h;
    }
    if (runResult) {
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
    if (panelRun && panelRun.status === 'completed' && hasResult(panelRun)) {
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
  // The role view draws the 실행 block only while the answer is not there: then the request is the anchor.
  const ownerOf = (index: number) => {
    const run = runs[index]!;
    return (splitsExecution ? layout.nodes.find((n) => n.kind === WorkKind && n.id === runNodeId(run, WorkSuffix)) : undefined)
      ?? layout.nodes.find((n) => n.kind === 'request' && n.id === runNodeId(run, 'request'));
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
