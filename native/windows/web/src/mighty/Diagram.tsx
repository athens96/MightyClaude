import { useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Icon } from '../icons/Icon';
import { useServices, useStoreState } from '../app/context';
import type { GraphBlockSize, MightyGraphRun, RunSession } from '../store/models';
import { Block, type BlockActions, type BlockExtra } from './Block';
import {
  FollowWatch, cameraOffset, cameraTween, centredX, dotFirst, dotRadius, dotStep, dotsShown, followTargetID, runsChanged, showsTop, trimAnchor,
  wheelScrollTarget, zoomAnchored, zoomIn, zoomLabel, zoomOut, ZoomDefault, ZoomMax, ZoomMin, type Point,
} from './camera';
import {
  finished, isAuxiliaryKind, isResizable, latestResultID, makeLayout, MaximumHeight, MaximumWidth, minimumBlockSize, normalizedSizeFor,
  nodeId, resultDrag, resultDragSides, runOf, WorkSuffix, type Layout, type Size,
} from './layout';
import { blockExtras } from './extras';
import { edgesCursor, pinnedAnchor, pinnedCamera, pinnedUnit, resizedSize, resizeTarget, type Edges } from './resize';
import { setBlockSize } from './prefs';
import { shareRuns } from './share';
import type { MightyBlockCopy, MightyPane, MightyFile } from './types';
import { ReferenceContext, useReferenceClick } from '../conversation/referenceClick';
import { holding, pendingRun, pendingRunId, requestState, runsFor, type PendingRequest } from '../pending/requests';
import { submitScroll } from '../pending/submitScroll';
import { Motion, reducesMotion } from '../theme/tokens';
import type { Translate } from '../i18n/locale';

const Overscan = 300;

/** A block the diagram opens on (MightyGraphCamera.Anchor.Reaim): selected, the camera on it; its request when it is not drawn. */
export interface Aim { nodeId: string; fallbackNodeId?: string }

interface Drag { id: string; edges: Edges; start: Size; origin: { x: number; y: number }; layout: Layout; target: 'result' | 'plan' | 'block' }


/** The edges as one path each (MightyGraph.cs edges): bottom-centre out, one turn 26 from the joining end, top-centre in under an arrow head. */
function edgePaths(layout: Layout, running: ReadonlySet<string>): { quiet: string; live: string } {
  const frames = new Map(layout.nodes.map((n) => [n.id, n.frame]));
  let quiet = '', live = '';
  for (const edge of layout.edges) {
    const from = frames.get(edge.source), to = frames.get(edge.target);
    if (!from || !to) continue;
    const sx = from.x + from.w / 2, sy = from.y + from.h, ex = to.x + to.w / 2, ey = to.y;
    const clearance = Math.min(26, Math.max(0, (ey - sy) / 2));
    const middle = edge.joins ? ey - clearance : sy + clearance;
    const d = `M${sx} ${sy}V${middle}H${ex}V${ey}M${ex - 4} ${ey - 6}L${ex} ${ey}L${ex + 4} ${ey - 6}`;
    if (running.has(edge.target)) live += d; else quiet += d;
  }
  return { quiet, live };
}

/**
 * The copy of a sent request's blocks until the host's come (natural submit B1): "다음 요청" with the pane's agent and no pill
 * (its 전송 중 is the block's pending mark), 실행 waiting for its answer, and for a request that did not start, 실행 stopped and
 * a failed result carrying the host's message.
 */
export function pendingCopies(request: PendingRequest, id: string, standIn: boolean, provider: string, t: Translate): Map<string, MightyBlockCopy> {
  const failed = request.phase === 'failed';
  const copy = (kind: string, title: string, status: string, tone: string): MightyBlockCopy =>
    ({ kind, title, state: '', status, tone, help: '', indicator: 'none', outline: 'none', fromRecord: false, provider: kind === 'request' ? provider : undefined });
  const copies = new Map([[nodeId(id, 'request'), copy('request', t('graph.block.nextRequest'), '', 'run')]]);
  // On the host's own run (it has arrived, its copy is on its way) only the request block is covered: its 실행 and result
  // wait for their own copy, so a run that finished at once never shows 요청 실패 meanwhile.
  if (standIn) copies.set(nodeId(id, WorkSuffix), copy('work', t('graph.block.execution'), failed ? 'stopped' : 'starting', failed ? 'stop' : 'idle'));
  if (failed) copies.set(nodeId(id, 'result'), copy('result', t('graph.block.resultError'), 'error', 'err'));
  return copies;
}

/**
 * The Mighty diagram (UI/MightyGraph.cs): the page's own layout of the pane's runs, its blocks as positioned cards in one
 * transformed layer, and one SVG under them for the dot grid and the edges. Pan, zoom and the follow camera move that layer
 * and the SVG's transforms directly, so moving the view draws no block; blocks draw only when their props change, and a
 * block off screen draws its header alone.
 */
export function Diagram({ session, mighty, aim, onAimed, pending: request, own = 0 }: {
  session: RunSession; mighty: MightyPane; aim?: Aim | null; onAimed?: () => void;
  /** The request the composer sent that the host's run has not replaced yet: drawn as the newest run. */
  pending?: PendingRequest;
  /** Counts the pane's own sends: each moves the camera to the new request over motion.scroll. */
  own?: number;
}) {
  const { bridge, t } = useServices();
  const state = useStoreState();
  const paneId = session.id;
  const inputs = mighty.inputs;
  const viewportRef = useRef<HTMLDivElement>(null);
  const worldRef = useRef<HTMLDivElement>(null);
  const edgesRef = useRef<SVGGElement>(null);
  const dotsRef = useRef<SVGPatternElement>(null);
  const dotRef = useRef<SVGCircleElement>(null);
  const dotsRect = useRef<SVGRectElement>(null);
  const camera = useRef<Point>({ x: 0, y: 0 });
  /** The camera's move to an own send's request: from where it was to where the follow puts it, over motion.scroll. */
  const tween = useRef<{ from: Point; to: Point; start: number; frame: number } | null>(null);
  const ownSeen = useRef(own);
  const watch = useRef(new FollowWatch());
  const runIds = useRef<string[] | null>(null);
  const shared = useRef<readonly MightyGraphRun[]>([]);
  const [viewport, setViewport] = useState<Size>({ w: 0, h: 0 });
  const [zoom, setZoom] = useState(ZoomDefault);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const [selected, setSelected] = useState<string | null>(null);
  const [visibleKey, setVisibleKey] = useState('');
  const [live, setLive] = useState<{ id: string; size: GraphBlockSize; target: Drag['target'] } | null>(null);
  const [pending, setPending] = useState<{ sizes: Record<string, GraphBlockSize> | null; base: unknown } | null>(null);
  const [measured, setMeasured] = useState<Record<string, number>>({});
  const drag = useRef<Drag | null>(null);
  /** The corner across from a drag's sides, kept at its place on screen while and after the drag until the view is moved. */
  const pin = useRef<{ id: string; unit: { x: number; y: number }; anchor: Point } | null>(null);
  // A file named in a block opens in the pane's reference bubble; kept in a ref so the blocks' actions keep their identity.
  const referenceShown = useContext(ReferenceContext);
  const showReference = useRef(referenceShown);
  showReference.current = referenceShown;
  const onReferenceClick = useReferenceClick();
  const loadingOlder = useRef<unknown>(null);

  const hostRuns = useMemo(() => {
    const next = shareRuns(shared.current, [...mighty.older, ...(session.graphRuns ?? [])]);
    shared.current = next;
    return next;
  }, [mighty.older, session.graphRuns]);
  // A sent request stands in as the newest run until the host's comes (natural submit B1); once the host's run is there, its
  // blocks wear the request's copy and 전송 중 until the host pushes their own copy (that comes up to ~120 ms after the run).
  const arrivedKey = request && request.phase !== 'failed' ? runsFor(request, session).join('\n') : '';
  const standIn = request !== undefined && arrivedKey === '';
  /** The run whose blocks carry the request's copy and pending mark: the stand-in, else the host's newest run for it. */
  const pendingRunIdOf = !request ? null : standIn ? pendingRunId(request) : arrivedKey.split('\n').pop() ?? null;
  const runs = useMemo(() => (request && standIn ? [...hostRuns, pendingRun(request, session.provider)] : hostRuns), [hostRuns, request, standIn, session.provider]);
  const copies = useMemo(() => (request && pendingRunIdOf ? pendingCopies(request, pendingRunIdOf, standIn, session.provider, t) : null), [request, pendingRunIdOf, standIn, session.provider, t]);
  const pendingState = useMemo(() => (request ? requestState(request) : null), [request]);

  // A released drag shows its size at once; the saved sizes coming back from the host take over.
  const savedSizes = pending && pending.base === session.graphBlockSizes ? pending.sizes : session.graphBlockSizes ?? null;
  const latest = latestResultID(runs);
  const blockSizes = useMemo(() => (live && live.target === 'block' ? { ...(savedSizes ?? {}), [live.id]: live.size } : savedSizes), [savedSizes, live]);
  const layout = useMemo(() => makeLayout(runs, session.draft, session.status === 'running', expanded, {
    resultFilesRunID: inputs?.resultFilesRunID ?? null,
    viewport: viewport.w > 0 && viewport.h > 0 ? viewport : null,
    zoom,
    sharedResultSize: live?.target === 'result' ? live.size : session.graphResultSize ?? null,
    retainedStart: inputs?.retainedStart ?? 0,
    history: inputs?.history !== undefined,
    resultContentHeight: latest && !live ? measured[latest] ?? null : null,
    blockSizes,
    planRunID: inputs?.planRunID ?? null,
    planRecords: inputs?.planRecords ?? [],
    planSize: live?.target === 'plan' ? live.size : session.graphPlanSize ?? null,
    splitsExecution: true,
    galleries: inputs?.galleries ?? [],
    executions: inputs?.executions ?? [],
  }), [runs, session.draft, session.status, expanded, inputs, viewport, zoom, live, session.graphResultSize, session.graphPlanSize, latest, measured, blockSizes]);
  const layoutRef = useRef(layout);
  layoutRef.current = layout;

  const running = useMemo(() => new Set(Object.entries(mighty.blocks).filter(([, b]) => b.tone === 'run').map(([id]) => id)), [mighty.blocks]);
  const edges = useMemo(() => edgePaths(layout, running), [layout, running]);

  // ── the camera: transforms set directly, the visible set recomputed only when it changes ──
  const frame = useRef(0);
  const updateVisible = useCallback(() => {
    frame.current = 0;
    const cam = camera.current, l = layoutRef.current, vp = viewportRef.current;
    if (!vp) return;
    const w = vp.clientWidth, h = vp.clientHeight;
    const z = zoomRef.current;
    const left = (-cam.x - Overscan) / z, top = (-cam.y - Overscan) / z, right = (w - cam.x + Overscan) / z, bottom = (h - cam.y + Overscan) / z;
    const ids = l.nodes.filter((n) => n.frame.x < right && n.frame.x + n.frame.w > left && n.frame.y < bottom && n.frame.y + n.frame.h > top).map((n) => n.id).join('\n');
    setVisibleKey((key) => (key === ids ? key : ids));
  }, []);
  const zoomRef = useRef(zoom);
  zoomRef.current = zoom;
  const applyCamera = useCallback(() => {
    const cam = camera.current, z = zoomRef.current;
    if (worldRef.current) worldRef.current.style.transform = `translate(${cam.x}px, ${cam.y}px) scale(${z})`;
    if (edgesRef.current) edgesRef.current.setAttribute('transform', `translate(${cam.x} ${cam.y}) scale(${z})`);
    const step = dotStep(z);
    if (dotsRef.current && dotRef.current) {
      dotsRef.current.setAttribute('width', String(step)); dotsRef.current.setAttribute('height', String(step));
      dotsRef.current.setAttribute('x', String(dotFirst(cam.x, step) - step / 2)); dotsRef.current.setAttribute('y', String(dotFirst(cam.y, step) - step / 2));
      dotRef.current.setAttribute('cx', String(step / 2)); dotRef.current.setAttribute('cy', String(step / 2)); dotRef.current.setAttribute('r', String(dotRadius(z)));
      dotsRect.current?.setAttribute('visibility', dotsShown(z) ? 'visible' : 'hidden');
    }
    if (!frame.current) frame.current = requestAnimationFrame(updateVisible);
  }, [updateVisible]);
  useEffect(() => () => { if (frame.current) cancelAnimationFrame(frame.current); }, []);

  const stopTween = useCallback(() => {
    if (tween.current) cancelAnimationFrame(tween.current.frame);
    tween.current = null;
  }, []);
  /** Moves the camera from `from` to `to` along the standard easing; each frame sets it and moves the layer. */
  const startTween = useCallback((from: Point, to: Point) => {
    stopTween();
    const move = { from, to, start: performance.now(), frame: 0 };
    tween.current = move;
    const step = () => {
      if (tween.current !== move) return;
      const progress = (performance.now() - move.start) / Motion.scroll;
      camera.current = cameraTween(move.from, move.to, progress);
      applyCamera();
      if (progress < 1) move.frame = requestAnimationFrame(step);
      else tween.current = null;
    };
    camera.current = from;
    move.frame = requestAnimationFrame(step);
  }, [applyCamera, stopTween]);
  useEffect(() => stopTween, [stopTween]);

  useLayoutEffect(() => {
    const element = viewportRef.current;
    if (!element) return;
    const observer = new ResizeObserver(() => setViewport((v) => (v.w === element.clientWidth && v.h === element.clientHeight ? v : { w: element.clientWidth, h: element.clientHeight })));
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const frameOf = (id: string | null) => (id === null ? null : layoutRef.current.nodes.find((n) => n.id === id)?.frame ?? null);

  // ── follow and the run-id rules after every layout pass (MightyGraphFollow, AimGraphCamera) ──
  useLayoutEffect(() => {
    if (viewport.w <= 0 || viewport.h <= 0) return;
    // Where the camera stands before this pass: an own send moves from here (a move under way, from where it is now).
    const before = { ...camera.current };
    const ownSend = own !== ownSeen.current;
    ownSeen.current = own;
    if (ownSend && submitScroll('diagram', true, true, watch.current.following).scroll === 'toRequest') watch.current.start();
    // The user's requests: a notice run (notice.ts) never starts following by itself; a following camera moves on to it.
    const ids = runs.filter((r) => r.noticeRun !== true).map((r) => r.id);
    const previous = runIds.current;
    const change = runsChanged(previous, ids, watch.current.following);
    runIds.current = ids;
    if (change === 'first' || change === 'follow') {
      watch.current.start();
      camera.current = { ...camera.current, x: centredX(viewport, zoom) };
    } else if (change === 'trim') {
      const anchor = trimAnchor(previous ?? [], ids, selected, new Set(layout.nodes.map((n) => n.id)));
      const target = anchor.kind === 'reaim' ? frameOf(anchor.nodeID) : null;
      if (anchor.kind === 'reaim' && target) camera.current = cameraOffset(target, viewport, zoom, anchor.alignTop);
    }
    const targetID = followTargetID(layout, runs);
    const next = watch.current.observe(targetID, frameOf(targetID), camera.current, viewport, zoom);
    if (next) camera.current = next;
    // A block asked for from another view: after the run-id rules, so a send is followed first and this wins.
    if (aim) {
      const id = frameOf(aim.nodeId) ? aim.nodeId : aim.fallbackNodeId && frameOf(aim.fallbackNodeId) ? aim.fallbackNodeId : null;
      const target = frameOf(id);
      if (id && target) {
        pin.current = null;
        camera.current = cameraOffset(target, viewport, zoom, false);
        watch.current.moved(targetID, frameOf(targetID), camera.current, viewport, zoom);
        setSelected(id);
        onAimed?.();
      }
    }
    // A resized block: the layout recentres it, the camera keeps the corner across from the dragged sides where it was.
    const pinned = pin.current;
    const pinnedFrame = pinned ? frameOf(pinned.id) : null;
    if (pinned && pinnedFrame) camera.current = pinnedCamera(pinned.anchor, pinned.unit, pinnedFrame, zoom);
    else pin.current = null;
    // An own send glides to its request (motion.scroll); Reduce Motion moves at once. A move under way keeps going to where
    // this pass put the camera.
    const moving = tween.current;
    if (ownSend && !aim && !reducesMotion()) startTween(before, camera.current);
    else if (moving && !aim && !ownSend) {
      // This pass moved the camera (the request grew, the follow went on): the move heads there instead.
      if (Math.abs(camera.current.x - before.x) > 0.5 || Math.abs(camera.current.y - before.y) > 0.5) moving.to = camera.current;
      camera.current = cameraTween(moving.from, moving.to, (performance.now() - moving.start) / Motion.scroll);
    }
    else if (moving) stopTween();
    applyCamera();
  }, [layout, viewport, zoom, aim, own]);

  const userMoved = useCallback(() => {
    pin.current = null;
    const l = layoutRef.current;
    const targetID = followTargetID(l, shared.current);
    watch.current.moved(targetID, frameOf(targetID), camera.current, { w: viewportRef.current?.clientWidth ?? 0, h: viewportRef.current?.clientHeight ?? 0 }, zoomRef.current);
    // Panning up to the history block reads older requests (LoadOlderAtTop).
    const history = l.nodes.find((n) => n.kind === 'history');
    if (history && inputsRef.current?.history?.acts && loadingOlder.current !== inputsRef.current && showsTop(camera.current.y, zoomRef.current, history.frame.y)) {
      loadingOlder.current = inputsRef.current;
      void bridge.call('mightyLoadOlder', { paneId });
    }
  }, [bridge, paneId]);
  const inputsRef = useRef(inputs);
  inputsRef.current = inputs;

  const pan = useCallback((dx: number, dy: number) => {
    stopTween();
    camera.current = { x: camera.current.x + dx, y: camera.current.y + dy };
    applyCamera();
  }, [applyCamera, stopTween]);

  // ── wheel: a selected or attached block under the pointer scrolls its body, anything else pans ──
  const selectedRef = useRef(selected);
  selectedRef.current = selected;
  useEffect(() => {
    const element = viewportRef.current;
    if (!element) return;
    const wheel = (event: WheelEvent) => {
      const hovered = (event.target as Element).closest?.('[data-block-id]') as HTMLElement | null;
      const id = hovered?.dataset.blockId ?? null;
      const kind = hovered?.dataset.kind ?? '';
      if (wheelScrollTarget(selectedRef.current, id, isAuxiliaryKind(kind)) !== null) return;
      event.preventDefault();
      const dx = event.deltaX !== 0 ? event.deltaX : event.shiftKey ? event.deltaY : 0;
      const dy = event.shiftKey && event.deltaX === 0 ? 0 : event.deltaY;
      pan(-dx, -dy);
      userMoved();
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => element.removeEventListener('wheel', wheel);
  }, [pan, userMoved]);

  // ── background drag pans and clears the selection; a press on a block selects it ──
  const panning = useRef<{ x: number; y: number; pointer: number } | null>(null);
  /** The block and sides a press at the client point would resize: the corner handle, then a band along each border. */
  const resizeAt = (clientX: number, clientY: number) => {
    const vp = viewportRef.current;
    if (!vp) return null;
    const box = vp.getBoundingClientRect(), cam = camera.current, z = zoomRef.current;
    const frames = layoutRef.current.nodes.filter((n) => isResizable(n.kind))
      .map((n) => ({ id: n.id, frame: { x: n.frame.x * z + cam.x, y: n.frame.y * z + cam.y, w: n.frame.w * z, h: n.frame.h * z } }));
    return resizeTarget(clientX - box.left, clientY - box.top, frames, z);
  };
  const resizeCursor = useRef('');
  const showResizeCursor = (cursor: string) => {
    const vp = viewportRef.current;
    if (!vp || resizeCursor.current === cursor) return;
    resizeCursor.current = cursor;
    vp.style.cursor = cursor;
    if (cursor) vp.dataset.resizeCursor = ''; else delete vp.dataset.resizeCursor;
  };
  const onPointerDown = (event: React.PointerEvent) => {
    if (event.button !== 0) return;
    const hit = resizeAt(event.clientX, event.clientY);
    if (hit && !(event.target as Element).closest('button')) { actions.startResize(hit.id, hit.edges, event); return; }
    const block = (event.target as Element).closest('[data-block-id]') as HTMLElement | null;
    if (block) {
      if (block.dataset.kind !== 'history') setSelected(block.dataset.blockId ?? null);
      return;
    }
    setSelected(null);
    panning.current = { x: event.clientX, y: event.clientY, pointer: event.pointerId };
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    viewportRef.current?.focus();
  };
  const onPointerMove = (event: React.PointerEvent) => {
    const d = drag.current;
    if (d) {
      const least = minimumBlockSize(d.layout.nodes.find((n) => n.id === d.id)?.kind ?? '');
      const dragged = resizedSize(d.edges, d.start, event.clientX - d.origin.x, event.clientY - d.origin.y, zoomRef.current, least, { w: MaximumWidth, h: MaximumHeight });
      const size = { width: dragged.w, height: dragged.h };
      if (d.target === 'block') setLive({ id: d.id, size: normalizedSizeFor(size, d.id) ?? size, target: 'block' });
      else {
        const limit = d.target === 'result' ? d.layout.resultLimit : d.layout.planLimit;
        const shown = resultDrag({ w: size.width, h: size.height }, true, true, 'live', null, limit, null).live;
        if (shown) setLive({ id: d.id, size: shown, target: d.target });
      }
      return;
    }
    const p = panning.current;
    if (!p) { showResizeCursor(edgesCursor(resizeAt(event.clientX, event.clientY)?.edges ?? 0)); return; }
    pan(event.clientX - p.x, event.clientY - p.y);
    panning.current = { ...p, x: event.clientX, y: event.clientY };
  };
  const endDrag = (event: React.PointerEvent | null, cancelled: boolean) => {
    const d = drag.current;
    if (d) {
      drag.current = null;
      const shown = live;
      setLive(null);
      if (cancelled || !shown || !event) return;
      const dragged = { w: shown.size.width, h: shown.size.height };
      const sides = resultDragSides(d.start, dragged);
      if (!sides.horizontal && !sides.vertical) return;
      watch.current.userChangedLayout();
      if (d.target === 'block') {
        setPending({ sizes: setBlockSize(session.graphBlockSizes, d.id, shown.size), base: session.graphBlockSizes });
        void bridge.call('mightySaveSize', { paneId, nodeId: d.id, target: 'block', width: shown.size.width, height: shown.size.height });
        return;
      }
      const saved = d.target === 'result' ? session.graphResultSize ?? null : session.graphPlanSize ?? null;
      const limit = d.target === 'result' ? d.layout.resultLimit : d.layout.planLimit;
      const fit = d.target === 'result' ? d.layout.resultWindowFit : d.layout.planWindowFit;
      const save = resultDrag(dragged, sides.horizontal, sides.vertical, 'finished', saved, limit, fit).save;
      if (save) void bridge.call('mightySaveSize', { paneId, nodeId: d.id, target: d.target, width: save.width, height: save.height });
      return;
    }
    if (panning.current) { panning.current = null; userMoved(); }
  };

  const actions = useMemo<BlockActions>(() => ({
    toggleExpand(id, open) {
      watch.current.userChangedLayout();
      setExpanded((s) => { const n = new Set(s); if (open) n.add(id); else n.delete(id); return n; });
      void bridge.call('mightyExpand', { paneId, nodeId: id, expanded: open });
    },
    startResize(id, edges, event) {
      if (event.button !== 0) return;
      event.stopPropagation();
      const l = layoutRef.current;
      const node = l.nodes.find((n) => n.id === id);
      if (!node) return;
      const target = id === latestResultID(shared.current) ? 'result' : node.kind === 'plan' ? 'plan' : 'block';
      drag.current = { id, edges, start: { w: node.frame.w, h: node.frame.h }, origin: { x: event.clientX, y: event.clientY }, layout: l, target };
      const unit = pinnedUnit(edges);
      pin.current = { id, unit, anchor: pinnedAnchor(unit, node.frame, camera.current, zoomRef.current) };
      watch.current.userChangedLayout();
      setSelected(id);
      viewportRef.current?.setPointerCapture(event.pointerId);
      viewportRef.current?.focus();
    },
    resetSize(id) {
      watch.current.userChangedLayout();
      const node = layoutRef.current.nodes.find((n) => n.id === id);
      const target = id === latestResultID(shared.current) ? 'result' : node?.kind === 'plan' ? 'plan' : 'block';
      setExpanded((s) => { if (!s.has(id)) return s; const n = new Set(s); n.delete(id); return n; });
      void bridge.call('mightySaveSize', { paneId, nodeId: id, target });
    },
    toggleFiles(runId) { void bridge.call('mightyResultFiles', { paneId, runId }); },
    fitResult() { void bridge.call('mightySaveSize', { paneId, nodeId: latestResultID(shared.current) ?? '', target: 'result' }); },
    openFile(file: MightyFile) { showReference.current?.({ path: file.path, line: file.line ?? null }); },
    loadOlder() { void bridge.call('mightyLoadOlder', { paneId }); },
    measured(id, height) { setMeasured((m) => (Math.abs((m[id] ?? -1) - height) < 1 ? m : { ...m, [id]: height })); },
  }), [bridge, paneId]);

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key !== 'Escape') return;
    if (drag.current) { drag.current = null; setLive(null); } else setSelected(null);
  };

  const setZoomTo = (next: number) => {
    const vp = { w: viewportRef.current?.clientWidth ?? 0, h: viewportRef.current?.clientHeight ?? 0 };
    pin.current = null;
    stopTween();
    camera.current = zoomAnchored(camera.current, vp, zoom, next);
    watch.current.stop();
    setZoom(next);
  };

  // ── each block's own parts, kept while they do not change ──
  const planText = useMemo(() => (state.cards[paneId] ?? []).find((c) => c.kind === 'plan') as { plan?: string } | undefined, [state.cards, paneId])?.plan;
  const extrasCache = useRef<ReadonlyMap<string, BlockExtra>>(new Map());
  const extras = useMemo(() => {
    const next = blockExtras({ layout, runs, inputs, draft: session.draft, planHistory: session.planHistory, resultSize: session.graphResultSize, planText, latest }, extrasCache.current);
    extrasCache.current = next;
    return next;
  }, [layout, runs, inputs, session.draft, session.planHistory, session.graphResultSize, planText, latest]);

  // The sent request's block carries its pending mark (전송 중, or its failure) in place of a pill.
  const drawnExtras = useMemo(() => {
    if (!request || !pendingState || !pendingRunIdOf) return extras;
    const id = nodeId(pendingRunIdOf, 'request');
    const extra = extras.get(id);
    // The host's own copy came: its block shows the run's state from now on, once a 전송 중 that has shown had its shortest
    // time (`holding`), which it keeps on that block meanwhile.
    if (!extra || (mighty.blocks[id] !== undefined && !holding(request))) return extras;
    const next = new Map(extras);
    next.set(id, { ...extra, pending: pendingState });
    return next;
  }, [extras, request, pendingState, pendingRunIdOf, mighty.blocks]);

  const visible = useMemo(() => new Set(visibleKey.split('\n')), [visibleKey]);
  const unfinished = useMemo(() => new Set(runs.filter((r) => !finished(r)).map((r) => r.id)), [runs]);
  const toolbarZoom = (
    <span className="mighty-zoom">
      <button className="mighty-icon" title={t('graph.zoom.out')} aria-label={t('graph.zoom.out')} disabled={zoom <= ZoomMin} onClick={() => setZoomTo(zoomOut(zoom))}><Icon name="zoom-out" size={15} weight={1.75} /></button>
      <button className="mighty-zoom-label" title={t('graph.zoom.reset')} onClick={() => setZoomTo(ZoomDefault)} data-testid="mighty-zoom">{zoomLabel(zoom)}</button>
      <button className="mighty-icon" title={t('graph.zoom.in')} aria-label={t('graph.zoom.in')} disabled={zoom >= ZoomMax} onClick={() => setZoomTo(zoomIn(zoom))}><Icon name="zoom-in" size={15} weight={1.75} /></button>
    </span>
  );

  return (
    <div className="mighty-diagram" data-testid="mighty-diagram" data-node-count={layout.nodes.length} data-edge-count={layout.edges.length}>
      <div className="mighty-diagram-zoom">{toolbarZoom}</div>
      <div className="mighty-viewport" ref={viewportRef} tabIndex={0} onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerLeave={() => { if (!drag.current) showResizeCursor(''); }}
        onPointerUp={(e) => endDrag(e, false)} onPointerCancel={() => endDrag(null, true)} onLostPointerCapture={() => { if (drag.current || panning.current) endDrag(null, !!drag.current); }}
        onKeyDown={onKeyDown}>
        <svg className="mighty-layer" aria-hidden="true">
          <defs>
            <pattern id={'mighty-dots-' + paneId} ref={dotsRef} patternUnits="userSpaceOnUse" width={18} height={18}>
              <circle ref={dotRef} cx={9} cy={9} r={1} className="mighty-dot" />
            </pattern>
          </defs>
          <rect ref={dotsRect} className="mighty-dots" width="100%" height="100%" fill={`url(#mighty-dots-${paneId})`} />
          <g ref={edgesRef} data-testid="mighty-edges">
            <path className="mighty-edges-quiet" d={edges.quiet} />
            <path className="mighty-edges-live" d={edges.live} />
          </g>
        </svg>
        <div className="mighty-world" ref={worldRef} onClick={onReferenceClick}>
          {layout.nodes.map((node) => (
            <Block key={node.id} paneId={paneId} provider={session.provider} id={node.id} kind={node.kind}
              x={node.frame.x} y={node.frame.y} w={node.frame.w} h={node.frame.h}
              copy={mighty.blocks[node.id] ?? copies?.get(node.id)} extra={drawnExtras.get(node.id) ?? {}} visible={visible.has(node.id)}
              selected={selected === node.id} expanded={expanded.has(node.id)}
              running={unfinished.has(runOf(node.id) ?? '')}
              actions={actions} />
          ))}
        </div>
      </div>
    </div>
  );
}

