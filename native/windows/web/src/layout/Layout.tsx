// The active workspace's layout (spec section 4; MainWindow.Layout.cs): the PaneLayoutNode tree the host resolved
// (EffectiveLayout, focus showing one group) as splits with draggable dividers and tab groups with their strips. A group keeps
// every pane it has shown mounted, hidden behind the selected one, so a tab switch draws no pane again. Every change of the
// tree is the host's: SelectLayoutSession, ActivatePane, ResizeSplit / ResetSplit, DockSession, CloseSession.
import { memo, useLayoutEffect, useRef, useState, type DragEvent, type PointerEvent as ReactPointerEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { ProviderMark } from '../conversation/ProviderMark';
import { PaneView } from '../pane/PaneView';
import { PaneShownContext } from '../pane/shown';
import { BetaBadge, StatusGlyph, useCall, useMenu, usePaneInfo, type MenuItem } from '../shell/parts';
import { useChoosePane } from '../shell/Sidebar';
import type { LayoutModel, PaneInfo } from '../shell/types';
import { activeSessionId } from '../store/store';
import type { PaneLayoutNode, RunSession } from '../store/models';
import { clampRatio, dropZone, layoutMetrics, layoutMinimum, type DropEdge } from './geometry';

/** The drag format of the WinUI tabs; the tab being dragged and its workspace, since a drag's data cannot be read while it hovers. */
export const paneDragFormat = 'application/x-dev.mightyclaude.pane-tab';
let dragged: { id: string; workspaceId: string } | null = null;

/** How far a press on a divider moves before it is a drag (MainWindow.Layout.cs DividerSlack). */
const dividerSlack = 3;

function KindIcon({ info }: { info: PaneInfo }) {
  if (info.kind === 'claude') return <ProviderMark provider={info.provider} size={11.5} />;
  const path = info.kind === 'shell' || info.kind === 'agent-terminal' ? 'M2 3h12v10H2zM4 6l2 2-2 2M8 10h4'
    : info.kind === 'files' ? 'M1.5 4h4.5l1.2 1.6h7.3V13h-13z' : 'M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM2 8h12M8 2c2 2 2 10 0 12M8 2c-2 2-2 10 0 12';
  return <svg width="10" height="10" viewBox="0 0 16 16" aria-hidden="true"><path d={path} fill="none" stroke="currentColor" strokeWidth="1.3" /></svg>;
}

interface TabProps {
  info: PaneInfo;
  selected: boolean;
  workspaceId: string;
  groupId: string;
  index: number;
}

const Tab = memo(function Tab({ info, selected, workspaceId, groupId, index }: TabProps) {
  const { t } = useServices();
  const call = useCall();
  const menu = useMenu();
  const choose = useChoosePane();
  const [landing, setLanding] = useState<'left' | 'right' | null>(null);
  const items: MenuItem[] = [
    { label: t('menu.rename'), action: () => call('renameSession', { id: info.id }) },
    { label: t('menu.focusPane'), action: () => call('focusPane', { id: info.id }) },
    { label: t('menu.closeTab'), action: () => call('closeSession', { id: info.id }) },
  ];
  const side = (event: DragEvent) => (event.clientX - event.currentTarget.getBoundingClientRect().left > event.currentTarget.clientWidth / 2 ? 'right' : 'left');
  return (
    <div className={'tab' + (selected ? ' selected' : '')} data-tab-id={info.id} data-testid="tab">
      <button type="button" className="tab-handle" draggable title={t('layout.tab.dragTooltip', { title: info.title })} aria-selected={selected} role="tab"
        onClick={() => choose(info.id)} onDoubleClick={() => call('renameSession', { id: info.id })} onContextMenu={(event) => menu.atPointer(event, items)}
        onDragStart={(event) => { dragged = { id: info.id, workspaceId }; event.dataTransfer.setData(paneDragFormat, info.id); event.dataTransfer.effectAllowed = 'move'; }}
        onDragEnd={() => { dragged = null; }}
        onDragOver={(event) => { if (dragged?.workspaceId !== workspaceId) return; event.preventDefault(); event.stopPropagation(); event.dataTransfer.dropEffect = 'move'; setLanding(side(event)); }}
        onDragLeave={() => setLanding(null)}
        onDrop={(event) => {
          if (dragged?.workspaceId !== workspaceId) return;
          event.preventDefault(); event.stopPropagation(); setLanding(null);
          const moved = dragged.id; dragged = null;
          call('dockSession', { sessionId: moved, workspaceId, groupId, edge: 'center', index: index + (side(event) === 'right' ? 1 : 0) });
        }}>
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
  const [hint, setHint] = useState<DropEdge | null>(null);
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
    return items;
  };
  const zone = (event: DragEvent<HTMLElement>): DropEdge => {
    const box = event.currentTarget.getBoundingClientRect();
    return dropZone(event.clientX - box.left, event.clientY - box.top, box.width, box.height);
  };
  return (
    <div className={'tab-group' + (holdsActive ? ' holds-active' : '')} data-group-id={node.id} data-testid="tab-group"
      onDragOver={(event) => { if (dragged?.workspaceId !== workspaceId) return; event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setHint(zone(event)); }}
      onDragLeave={(event) => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setHint(null); }}
      onDrop={(event) => {
        if (dragged?.workspaceId !== workspaceId) return;
        event.preventDefault(); setHint(null);
        const moved = dragged.id; dragged = null;
        call('dockSession', { sessionId: moved, workspaceId, groupId: node.id, edge: zone(event) });
      }}>
      <div className="tab-strip" ref={strip} role="tablist">
        {node.sessionIds.map((id, index) => {
          const info = panes.get(id);
          return info ? <Tab key={id} info={info} selected={id === selected} workspaceId={workspaceId} groupId={node.id} index={index} /> : null;
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
    if (!drag.moved && Math.abs(change) < dividerSlack) return;
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
    </div>
  );
}
