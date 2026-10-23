import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useServices, useStoreState } from '../app/context';
import type { GraphBlockSize, MightyGraphRun, RunSession } from '../store/models';
import { Block, type BlockActions, type BlockExtra } from './Block';
import {
  FollowWatch, cameraOffset, centredX, dotFirst, dotRadius, dotStep, dotsShown, followTargetID, runsChanged, showsTop, trimAnchor,
  wheelScrollTarget, zoomAnchored, zoomIn, zoomLabel, zoomOut, ZoomDefault, ZoomMax, ZoomMin, type Point,
} from './camera';
import {
  ImagesSuffix, PlanRecordSuffix, finished, isAuxiliaryKind, latestResultID, makeLayout, minimumBlockSize, normalizedSizeFor,
  resultDrag, resultDragSides, runOf, suffixOf, type Layout, type Size,
} from './layout';
import { setBlockSize } from './prefs';
import { shareRuns } from './share';
import type { MightyPane, MightyFile } from './types';

const Overscan = 300;

interface Drag { id: string; edges: 'right' | 'bottom' | 'corner'; start: Size; origin: { x: number; y: number }; layout: Layout; target: 'result' | 'plan' | 'block' }

function sameExtra(a: BlockExtra, b: BlockExtra): boolean {
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]) as Set<keyof BlockExtra>;
  for (const key of keys) if (a[key] !== b[key]) return false;
  return true;
}

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
 * The Mighty diagram (UI/MightyGraph.cs): the page's own layout of the pane's runs, its blocks as positioned cards in one
 * transformed layer, and one SVG under them for the dot grid and the edges. Pan, zoom and the follow camera move that layer
 * and the SVG's transforms directly, so moving the view draws no block; blocks draw only when their props change, and a
 * block off screen draws its header alone.
 */
export function Diagram({ session, mighty }: { session: RunSession; mighty: MightyPane }) {
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
  const loadingOlder = useRef<unknown>(null);

  const runs = useMemo(() => {
    const next = shareRuns(shared.current, [...mighty.older, ...(session.graphRuns ?? [])]);
    shared.current = next;
    return next;
  }, [mighty.older, session.graphRuns]);

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
    const ids = runs.map((r) => r.id);
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
    applyCamera();
  }, [layout, viewport, zoom]);

  const userMoved = useCallback(() => {
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
    camera.current = { x: camera.current.x + dx, y: camera.current.y + dy };
    applyCamera();
  }, [applyCamera]);

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
  const onPointerDown = (event: React.PointerEvent) => {
    if (event.button !== 0) return;
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
      const z = zoomRef.current;
      const w = d.edges === 'bottom' ? d.start.w : d.start.w + (event.clientX - d.origin.x) / z;
      const h = d.edges === 'right' ? d.start.h : d.start.h + (event.clientY - d.origin.y) / z;
      const least = minimumBlockSize(d.layout.nodes.find((n) => n.id === d.id)?.kind ?? '');
      const size = { width: Math.max(least.w, w), height: Math.max(least.h, h) };
      if (d.target === 'block') setLive({ id: d.id, size: normalizedSizeFor(size, d.id) ?? size, target: 'block' });
      else {
        const limit = d.target === 'result' ? d.layout.resultLimit : d.layout.planLimit;
        const shown = resultDrag({ w: size.width, h: size.height }, true, true, 'live', null, limit, null).live;
        if (shown) setLive({ id: d.id, size: shown, target: d.target });
      }
      return;
    }
    const p = panning.current;
    if (!p) return;
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
    openFile(file: MightyFile) { void bridge.call('openReference', { paneId, path: file.path, line: file.line ?? null }); },
    loadOlder() { void bridge.call('mightyLoadOlder', { paneId }); },
    measured(id, height) { setMeasured((m) => (Math.abs((m[id] ?? -1) - height) < 1 ? m : { ...m, [id]: height })); },
  }), [bridge, paneId]);

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key !== 'Escape') return;
    if (drag.current) { drag.current = null; setLive(null); } else setSelected(null);
  };

  const setZoomTo = (next: number) => {
    const vp = { w: viewportRef.current?.clientWidth ?? 0, h: viewportRef.current?.clientHeight ?? 0 };
    camera.current = zoomAnchored(camera.current, vp, zoom, next);
    watch.current.stop();
    setZoom(next);
  };

  // ── each block's own parts, kept while they do not change ──
  const planText = useMemo(() => (state.cards[paneId] ?? []).find((c) => c.kind === 'plan') as { plan?: string } | undefined, [state.cards, paneId])?.plan;
  const extrasCache = useRef(new Map<string, BlockExtra>());
  const extras = useMemo(() => {
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
          extra = { entries: run?.resultEntries, finalOutput: run?.finalOutput, files, filesOpen: inputs?.resultFilesRunID === run?.id, fitted: layout.fittedResultID === node.id && !session.graphResultSize, measure: node.id === latest };
          break;
        }
        case 'draft': extra = { draft: session.draft }; break;
        case 'plan': extra = { markdown: planText }; break;
        case 'planRecord': extra = { markdown: session.planHistory?.find((r) => r.id === suffix.slice(PlanRecordSuffix.length))?.plan }; break;
        case 'resultFiles': extra = { files: run ? inputs?.files[run.id] : undefined }; break;
        case 'images': { const step = suffix.slice(ImagesSuffix.length); extra = { images: run ? { runId: run.id, step, count: inputs?.galleries.find((g) => g.runID === run.id && g.step === step)?.count ?? 0 } : undefined }; break; }
        case 'execution': extra = { execution: inputs?.executionCards[node.id] }; break;
        case 'history': extra = { history: inputs?.history }; break;
      }
      const before = extrasCache.current.get(node.id);
      if (before && extra.images && before.images && before.images.runId === extra.images.runId && before.images.step === extra.images.step && before.images.count === extra.images.count) extra = { ...extra, images: before.images };
      next.set(node.id, before && sameExtra(before, extra) ? before : extra);
    }
    extrasCache.current = next;
    return next;
  }, [layout, runs, inputs, session.draft, session.planHistory, session.graphResultSize, planText, latest]);

  const visible = useMemo(() => new Set(visibleKey.split('\n')), [visibleKey]);
  const unfinished = useMemo(() => new Set(runs.filter((r) => !finished(r)).map((r) => r.id)), [runs]);
  const toolbarZoom = (
    <span className="mighty-zoom">
      <button className="mighty-icon" title={t('graph.zoom.out')} disabled={zoom <= ZoomMin} onClick={() => setZoomTo(zoomOut(zoom))}>−</button>
      <button className="mighty-zoom-label" title={t('graph.zoom.reset')} onClick={() => setZoomTo(ZoomDefault)} data-testid="mighty-zoom">{zoomLabel(zoom)}</button>
      <button className="mighty-icon" title={t('graph.zoom.in')} disabled={zoom >= ZoomMax} onClick={() => setZoomTo(zoomIn(zoom))}>+</button>
    </span>
  );

  return (
    <div className="mighty-diagram" data-testid="mighty-diagram" data-node-count={layout.nodes.length} data-edge-count={layout.edges.length}>
      <div className="mighty-diagram-zoom">{toolbarZoom}</div>
      <div className="mighty-viewport" ref={viewportRef} tabIndex={0} onPointerDown={onPointerDown} onPointerMove={onPointerMove}
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
        <div className="mighty-world" ref={worldRef}>
          {layout.nodes.map((node) => (
            <Block key={node.id} paneId={paneId} provider={session.provider} id={node.id} kind={node.kind}
              x={node.frame.x} y={node.frame.y} w={node.frame.w} h={node.frame.h}
              copy={mighty.blocks[node.id]} extra={extras.get(node.id) ?? {}} visible={visible.has(node.id)}
              selected={selected === node.id} expanded={expanded.has(node.id)}
              running={unfinished.has(runOf(node.id) ?? '')}
              actions={actions} />
          ))}
        </div>
      </div>
    </div>
  );
}

