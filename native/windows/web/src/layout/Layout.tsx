// The active workspace's layout (spec section 4; MainWindow.Layout.cs): the PaneLayoutNode tree the host resolved
// (EffectiveLayout, focus showing one group) as splits with draggable dividers and tab groups with their strips. A group keeps
// every pane it has shown mounted, hidden behind the selected one, so a tab switch draws no pane again. Every change of the
// tree is the host's: SelectLayoutSession, ActivatePane, ResizeSplit / ResetSplit, DockSession, CloseSession.
import { memo, useLayoutEffect, useRef, useState, useSyncExternalStore, type PointerEvent as ReactPointerEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { PaneView } from '../pane/PaneView';
import { PaneShownContext } from '../pane/shown';
import { BetaBadge, StatusGlyph, useCall, useMenu, usePaneInfo, type MenuItem } from '../shell/parts';
import { useChoosePane } from '../shell/Sidebar';
import type { LayoutModel, PaneInfo } from '../shell/types';
import { activeSessionId } from '../store/store';
import type { PaneLayoutNode, RunSession } from '../store/models';
import { usePageOverlay } from '../shell/overlay';
import { clampRatio, dragSlack, layoutMetrics, layoutMinimum, type DropEdge } from './geometry';
import { dockArguments, resolveTabDrop, startTabPress, stepTabPress, stripScroll, type TabDragHit, type TabDragTarget, type TabPress } from './tabDrag';

/** The tab being moved (tabDrag.ts): its press, and the pointer's spot and target once past the slack. One at a time. */
interface TabDrag { id: string; workspaceId: string; title: string; press: TabPress }
let tabDrag: TabDrag | null = null;
/** A press that became a drag still ends in a click on its handle; that click is not a choice. */
let swallowClick = false;
const dragListeners = new Set<() => void>();
const subscribeDrag = (listener: () => void) => { dragListeners.add(listener); return () => { dragListeners.delete(listener); }; };
const dragging = () => (tabDrag?.press.phase === 'dragging' ? tabDrag : null);
function setTabDrag(next: TabDrag | null) {
  const was = dragging();
  tabDrag = next;
  if (was || dragging()) for (const listener of dragListeners) listener();
}

const boxOf = (element: Element) => { const r = element.getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; };

/** The tab handle, the rest of a strip or the tab group under the pointer (the ghost, its cover and the hints take no pointer). */
function hitAt(x: number, y: number): TabDragHit {
  const under = document.elementFromPoint(x, y);
  const group = under?.closest<HTMLElement>('.tab-group[data-group-id]');
  if (!under || !group) return null;
  const workspaceId = group.dataset.workspaceId ?? '';
  const groupId = group.dataset.groupId!;
  const handle = under.closest<HTMLElement>('.tab-handle');
  const tab = handle?.closest<HTMLElement>('.tab[data-tab-id]');
  if (handle && tab && group.contains(tab)) return { kind: 'tab', workspaceId, groupId, tabId: tab.dataset.tabId!, index: Number(tab.dataset.tabIndex), box: boxOf(handle) };
  const strip = under.closest('.tab-strip');
  if (strip && group.contains(strip)) return { kind: 'strip', workspaceId, groupId };
  return { kind: 'group', workspaceId, groupId, box: boxOf(group) };
}

/** An overflowing strip under a dragging pointer near its end scrolls toward it. */
function scrollStripAt(x: number, y: number) {
  const strip = document.elementFromPoint(x, y)?.closest<HTMLElement>('.tab-strip');
  if (!strip || strip.scrollWidth <= strip.clientWidth) return;
  const r = strip.getBoundingClientRect();
  strip.scrollLeft += stripScroll(x, r.left, r.right);
}

/** A left press on a tab handle: window listeners follow it until release, Esc or a cancel; past the slack it holds the pointer.
 *  Every end runs `finish` once, which takes every listener, the capture, the drag class and the drag itself away. */
function pressTab(event: ReactPointerEvent<HTMLElement>, id: string, workspaceId: string, title: string, dock: (target: TabDragTarget) => void) {
  if (event.button !== 0 || !event.isPrimary || tabDrag) return;
  swallowClick = false;
  const handle = event.currentTarget;
  const pointerId = event.pointerId;
  setTabDrag({ id, workspaceId, title, press: startTabPress(event.clientX, event.clientY) });
  const finish = (dragged: boolean) => {
    window.removeEventListener('pointermove', move, true);
    window.removeEventListener('pointerup', up, true);
    window.removeEventListener('pointercancel', cancel, true);
    window.removeEventListener('lostpointercapture', cancel, true);
    window.removeEventListener('keydown', key, true);
    window.removeEventListener('blur', cancel);
    document.documentElement.classList.remove('tab-dragging');
    if (handle.hasPointerCapture(pointerId)) handle.releasePointerCapture(pointerId);
    setTabDrag(null);
    // Only the click that ends this very press is not a choice; a later one (a key's Enter or Space included) is.
    swallowClick = dragged;
    if (dragged) setTimeout(() => { swallowClick = false; }, 0);
  };
  const move = (e: PointerEvent) => {
    if (e.pointerId !== pointerId || !tabDrag) return;
    const target = resolveTabDrop(hitAt(e.clientX, e.clientY), e.clientX, e.clientY, workspaceId);
    const step = stepTabPress(tabDrag.press, { type: 'move', x: e.clientX, y: e.clientY, buttons: e.buttons, target });
    // The button came up where the page did not hear it: the drag ends as a cancel, never as a later click's landing.
    if (!step.press) { finish(tabDrag.press.phase === 'dragging'); return; }
    if (step.press.phase === 'dragging' && tabDrag.press.phase === 'pressed') {
      if (handle.isConnected) handle.setPointerCapture(pointerId);
      document.documentElement.classList.add('tab-dragging');
    }
    setTabDrag({ ...tabDrag, press: step.press });
    if (step.press.phase === 'dragging') scrollStripAt(e.clientX, e.clientY);
  };
  const up = (e: PointerEvent) => {
    if (e.pointerId !== pointerId || !tabDrag) return;
    const step = stepTabPress(tabDrag.press, { type: 'up', target: resolveTabDrop(hitAt(e.clientX, e.clientY), e.clientX, e.clientY, workspaceId) });
    finish(step.end !== 'click');
    if (step.end === 'drop') dock(step.target);
  };
  const cancel = (e: Event) => {
    if (e instanceof PointerEvent && e.pointerId !== pointerId) return;
    if (!tabDrag) return;
    finish(tabDrag.press.phase === 'dragging');
  };
  const key = (e: KeyboardEvent) => {
    if (e.key !== 'Escape' || !dragging()) return;
    e.preventDefault(); e.stopPropagation();
    cancel(e);
  };
  window.addEventListener('pointermove', move, true);
  window.addEventListener('pointerup', up, true);
  window.addEventListener('pointercancel', cancel, true);
  // The handle losing the pointer (taken away, or the handle drawn again) ends the drag as a cancel.
  window.addEventListener('lostpointercapture', cancel, true);
  window.addEventListener('keydown', key, true);
  window.addEventListener('blur', cancel);
}

/** The moving tab's title beside the pointer, so the drag shows what it carries; dimmed where it cannot land. While it drags,
 *  a page overlay over the dock (shell/overlay.ts) steps the browser panes' native controls aside, so the ghost and the hints
 *  show over them too and the pointer stays the page's. */
function TabDragGhost() {
  const drag = useSyncExternalStore(subscribeDrag, dragging);
  usePageOverlay(drag !== null);
  if (!drag) return null;
  return (
    <>
      <div className="tab-drag-cover" data-page-overlay aria-hidden="true" />
      <div className={'tab-drag-ghost' + (drag.press.target ? '' : ' nowhere')} data-testid="tab-drag-ghost" aria-hidden="true"
        style={{ transform: `translate(${drag.press.x}px, ${drag.press.y}px)` }}>{drag.title}</div>
    </>
  );
}

function KindIcon({ info }: { info: PaneInfo }) {
  if (info.kind === 'claude') return <ProviderMark provider={info.provider} size={11.5} />;
  const path = info.kind === 'shell' || info.kind === 'agent-terminal' ? 'M2 3h12v10H2zM4 6l2 2-2 2M8 10h4'
    : info.kind === 'files' ? 'M1.5 4h4.5l1.2 1.6h7.3V13h-13z' : info.kind === 'workmap' ? 'M1.5 3.5 5.5 2v10.5l-4 1.5zM5.5 2l5 1.5V14l-5-1.5zM10.5 3.5l4-1.5v10.5l-4 1.5z' : 'M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM2 8h12M8 2c2 2 2 10 0 12M8 2c-2 2-2 10 0 12';
  return <svg width="10" height="10" viewBox="0 0 16 16" aria-hidden="true"><path d={path} fill="none" stroke="currentColor" strokeWidth="1.3" /></svg>;
}

interface TabProps {
  info: PaneInfo;
  selected: boolean;
  workspaceId: string;
  index: number;
}

const Tab = memo(function Tab({ info, selected, workspaceId, index }: TabProps) {
  const { t } = useServices();
  const call = useCall();
  const menu = useMenu();
  const choose = useChoosePane();
  const landing = useSyncExternalStore(subscribeDrag, () => { const target = dragging()?.press.target; return target?.tabId === info.id ? target.side ?? null : null; });
  const items: MenuItem[] = [
    { label: t('menu.rename'), action: () => call('renameSession', { id: info.id }) },
    { label: t('menu.focusPane'), action: () => call('focusPane', { id: info.id }) },
    { label: t('menu.closeTab'), action: () => call('closeSession', { id: info.id }) },
  ];
  return (
    <div className={'tab' + (selected ? ' selected' : '')} data-tab-id={info.id} data-tab-index={index} data-testid="tab">
      <button type="button" className="tab-handle" title={t('layout.tab.dragTooltip', { title: info.title })} aria-selected={selected} role="tab"
        onClick={(event) => { if (swallowClick && event.detail > 0) { swallowClick = false; return; } choose(info.id); }}
        onDoubleClick={() => call('renameSession', { id: info.id })} onContextMenu={(event) => menu.atPointer(event, items)}
        onPointerDown={(event) => pressTab(event, info.id, workspaceId, info.title, (target) => call('dockSession', dockArguments(info.id, workspaceId, target)))}>
        <KindIcon info={info} />
        <span className="tab-title" title={info.help}>{info.title}</span>
        {info.beta && <BetaBadge />}
        {info.tabMark && <StatusGlyph tone={info.tone} size={12} />}
        {landing && <span className={'tab-landing ' + landing} data-testid="tab-landing" />}
      </button>
      <button type="button" className="tab-close" aria-label={t('layout.tab.closeAccessibility', { title: info.title })} onClick={() => call('closeSession', { id: info.id })}>×</button>
    </div>
  );
});

const hintKey: Record<DropEdge, string> = { center: 'layout.drop.merge', left: 'layout.drop.left', right: 'layout.drop.right', top: 'layout.drop.top', bottom: 'layout.drop.bottom' };

function TabGroup({ node, workspaceId, sessions, activeId }: { node: PaneLayoutNode; workspaceId: string; sessions: Map<string, RunSession>; activeId?: string }) {
  const { t } = useServices();
  const call = useCall();
  const menu = useMenu();
  const choose = useChoosePane();
  const panes = usePaneInfo();
  const strip = useRef<HTMLDivElement>(null);
  // Over a tab the landing mark shows instead of the group's hint.
  const hint = useSyncExternalStore(subscribeDrag, () => { const target = dragging()?.press.target; return target?.groupId === node.id && target.tabId === undefined ? target.edge : null; });
  // The pane the click chose shows at once; otherwise the group's own selection.
  const selected = activeId && node.sessionIds.includes(activeId) ? activeId : node.selectedSessionId ?? node.sessionIds[0];
  // Every pane this group has shown stays mounted while it is one of its tabs.
  const seen = useRef(new Set<string>());
  if (selected) seen.current.add(selected);
  for (const id of seen.current) if (!node.sessionIds.includes(id)) seen.current.delete(id);
  const holdsActive = activeId !== undefined && node.sessionIds.includes(activeId);
  // A strip with more tabs than room shows the selected one in its middle.
  useLayoutEffect(() => {
    const tab = strip.current?.querySelector<HTMLElement>('[data-tab-id="' + selected + '"]');
    const bar = strip.current;
    if (!tab || !bar || bar.scrollWidth <= bar.clientWidth) return;
    bar.scrollLeft = Math.max(0, tab.offsetLeft + tab.offsetWidth / 2 - bar.clientWidth / 2);
  }, [selected, node.sessionIds.length]);
  const paneMenu = (id: string): MenuItem[] => {
    const items: MenuItem[] = [
      { label: t('menu.rename'), action: () => call('renameSession', { id }) },
      { label: t('session.menu.focus'), action: () => call('focusPane', { id, toggle: true }) },
      { label: t('session.menu.close'), action: () => call('closeSession', { id }) },
    ];
    // A shell's history of what its pane recorded (Navigation.cs:352-364), before the close.
    if (sessions.get(id)?.kind === 'shell') items.splice(2, 0, { label: t('terminal.history.title'), action: () => call('terminalHistory', { paneId: id }) });
    return items;
  };
  return (
    <div className={'tab-group' + (holdsActive ? ' holds-active' : '')} data-group-id={node.id} data-workspace-id={workspaceId} data-testid="tab-group">
      <div className="tab-strip" ref={strip} role="tablist">
        {node.sessionIds.map((id, index) => {
          const info = panes.get(id);
          return info ? <Tab key={id} info={info} selected={id === selected} workspaceId={workspaceId} index={index} /> : null;
        })}
      </div>
      <div className="tab-body">
        {[...seen.current].map((id) => {
          const session = sessions.get(id);
          if (!session) return null;
          const shown = id === selected;
          return (
            <div key={id} className={'pane-slot' + (shown ? '' : ' hidden') + (id === activeId ? ' active' : '')} data-pane-id={id} aria-hidden={!shown}
              onPointerDownCapture={() => { if (activeId !== id) choose(id, 'activatePane'); }}
              onContextMenu={(event) => { if ((event.target as Element).closest('.pane-header')) menu.atPointer(event, paneMenu(id)); }}>
              <PaneShownContext.Provider value={shown}>
                <PaneView session={session} />
              </PaneShownContext.Provider>
            </div>
          );
        })}
      </div>
      {hint && (
        <div className={'drop-hint ' + hint} data-testid="drop-hint"><span className="drop-hint-label">{t(hintKey[hint])}</span></div>
      )}
    </div>
  );
}

function Split({ node, workspaceId, sessions, activeId }: { node: PaneLayoutNode; workspaceId: string; sessions: Map<string, RunSession>; activeId?: string }) {
  const { t } = useServices();
  const call = useCall();
  const horizontal = node.axis === 'horizontal';
  const [live, setLive] = useState<number | null>(null);
  const element = useRef<HTMLDivElement>(null);
  const press = useRef<{ start: number; ratio: number; moved: boolean } | null>(null);
  const ratio = live ?? node.ratio;
  const first = layoutMinimum(node.children[0]!);
  const second = layoutMinimum(node.children[1]!);
  const down = (event: ReactPointerEvent<HTMLDivElement>) => {
    event.currentTarget.setPointerCapture(event.pointerId);
    press.current = { start: horizontal ? event.clientX : event.clientY, ratio: node.ratio, moved: false };
  };
  const move = (event: ReactPointerEvent<HTMLDivElement>) => {
    const drag = press.current; const box = element.current?.getBoundingClientRect();
    if (!drag || !box) return;
    const change = (horizontal ? event.clientX : event.clientY) - drag.start;
    if (!drag.moved && Math.abs(change) < dragSlack) return;
    drag.moved = true;
    const size = (horizontal ? box.width : box.height) - layoutMetrics.splitDivider;
    if (size > 0) setLive(clampRatio(drag.ratio + change / size));
  };
  const up = () => {
    const drag = press.current; press.current = null;
    if (!drag?.moved || live === null) { setLive(null); return; }
    call('resizeSplit', { workspaceId, splitId: node.id, ratio: live });
    // The host's tree comes back with this share; until then the live one stands.
    setTimeout(() => setLive(null), 400);
  };
  const style = horizontal
    ? { gridTemplateColumns: `minmax(${first.width}px, ${ratio}fr) ${layoutMetrics.splitDivider}px minmax(${second.width}px, ${1 - ratio}fr)` }
    : { gridTemplateRows: `minmax(${first.height}px, ${ratio}fr) ${layoutMetrics.splitDivider}px minmax(${second.height}px, ${1 - ratio}fr)` };
  return (
    <div ref={element} className={'split ' + (horizontal ? 'horizontal' : 'vertical')} style={style} data-split-id={node.id} data-testid="split">
      <LayoutNode node={node.children[0]!} workspaceId={workspaceId} sessions={sessions} activeId={activeId} />
      <div className={'split-divider' + (live !== null ? ' dragging' : '')} role="separator" aria-orientation={horizontal ? 'vertical' : 'horizontal'}
        aria-label={t(horizontal ? 'layout.divider.horizontal' : 'layout.divider.vertical')} title={t('layout.divider.tooltip')}
        onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={() => { press.current = null; setLive(null); }}
        onDoubleClick={() => call('resetSplit', { workspaceId, splitId: node.id })}>
        <span className="split-handle" />
      </div>
      <LayoutNode node={node.children[1]!} workspaceId={workspaceId} sessions={sessions} activeId={activeId} />
    </div>
  );
}

function LayoutNode(props: { node: PaneLayoutNode; workspaceId: string; sessions: Map<string, RunSession>; activeId?: string }) {
  if (props.node.kind === 'tabs' || props.node.children.length < 2) return <TabGroup key={props.node.id} {...props} />;
  return <Split key={props.node.id} {...props} />;
}

/** The dock: the tree in a scroll area at least as large as its panes' minimums (RenderPaneLayout). */
export function Layout({ layout }: { layout: LayoutModel }) {
  const state = useStoreState();
  const sessions = useRef<{ list: RunSession[] | undefined; map: Map<string, RunSession> }>({ list: undefined, map: new Map() });
  const list = state.snapshot?.sessions;
  if (sessions.current.list !== list) sessions.current = { list, map: new Map((list ?? []).map((s) => [s.id, s])) };
  const minimum = layoutMinimum(layout.root);
  return (
    <div className="dock" data-testid="layout" data-mode={layout.mode}>
      <div className="dock-content" style={{ minWidth: minimum.width, minHeight: minimum.height }}>
        <LayoutNode node={layout.root} workspaceId={layout.workspaceId} sessions={sessions.current.map} activeId={activeSessionId(state)} />
      </div>
      <TabDragGhost />
    </div>
  );
}
