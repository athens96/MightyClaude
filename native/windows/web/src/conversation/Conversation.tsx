import { memo, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import type { AgentActivity, LogEntry, RunSession } from '../store/models';
import { Entry, type EntryProps } from './Entry';
import { providerLabel } from './format';
import { ProviderMark } from './ProviderMark';
import { ReferenceContext, clickedAt, linkAt, type Clicked } from './referenceClick';
import { HeightCache, estimateHeight, layout, nearBottom, visibleRange } from './virtual';
import { usePageOverlay } from '../shell/overlay';
import { Pending, PendingBars } from '../pending/Pending';
import { requestState, shownInConversation, type PendingRequest } from '../pending/requests';
import { submitScroll } from '../pending/submitScroll';

interface MenuState {
  x: number;
  y: number;
  selection: string;
  code: string | null;
}

/** A request the composer sent that the host has not brought yet (natural submit B1): its words, then 전송 중 or its failure. */
const PendingBubble = memo(function PendingBubble({ request }: { request: PendingRequest }) {
  const state = useMemo(() => requestState(request), [request]);
  return (
    <div className={'entry entry-user entry-pending' + (request.phase === 'failed' ? ' failed' : '')} data-testid="pending-request" data-pending-request={request.clientId}>
      <div className="user-bubble"><div className="user-text" data-text="">{request.text}</div></div>
      <div className="entry-pending-mark"><Pending state={state} /></div>
    </div>
  );
});

/** "실행 중" from the run's start until its first output, with what the agent does when the host said (its latest activity). */
function RunningRow({ activity }: { activity: AgentActivity | undefined }) {
  const { t } = useServices();
  const doing = activity ? [activity.toolName, activity.summary].filter((part) => part && part.trim().length > 0).join(' · ') : '';
  return (
    <div className="entry entry-running" data-testid="running-row" role="status">
      <PendingBars />
      <span className="entry-running-word">{t('graph.state.running')}</span>
      {doing && <span className="entry-running-activity" title={doing}>{doing}</span>}
    </div>
  );
}

/** The pane runs and nothing has come since its request: no reply, no tool call, no line. */
export function awaitsFirstOutput(session: RunSession): boolean {
  if (session.status !== 'running') return false;
  const last = session.logs[session.logs.length - 1];
  return last !== undefined && last.kind === 'user';
}

const Row = memo(function Row({ observe, ...props }: EntryProps & { observe: (element: HTMLDivElement | null) => (() => void) | undefined }) {
  return (
    <div className="entry-row" data-entry-id={props.entry.id} ref={observe}>
      <Entry {...props} />
    </div>
  );
});

/**
 * The selected agent pane's conversation (spec §2), windowed: only entries near the viewport are in the page, each measured
 * once per width (HeightCache) and the rest stood in for by their heights. A settled entry keeps its props, so it never draws
 * again; the one that streams draws alone. The view follows the bottom only while it is within 32 px of it.
 */
export function Conversation({ session }: { session: RunSession }) {
  const { bridge, t } = useServices();
  const stored = useStoreState();
  const logs = session.logs;
  const requests = shownInConversation(stored.pending[session.id], session);
  const running = awaitsFirstOutput(session) && requests.length === 0;
  const activity = stored.activities[session.id];
  const own = stored.ownSubmits[session.id] ?? 0;
  const scroller = useRef<HTMLDivElement>(null);
  const [heights] = useState(() => new HeightCache());
  const following = useRef(true);
  const width = useRef(0);
  const frame = useRef(0);
  // Starts at the end: a pane opens on its newest entries.
  const [view, setView] = useState({ top: Number.MAX_SAFE_INTEGER, height: 600, width: 0 });
  const [version, setVersion] = useState(0);
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set());
  const [menu, setMenu] = useState<MenuState | null>(null);
  usePageOverlay(menu !== null);

  const stickToBottom = useCallback(() => {
    const element = scroller.current;
    if (element && following.current) element.scrollTop = element.scrollHeight;
  }, []);

  const [rows] = useState(
    () =>
      new ResizeObserver((records) => {
        let changed = false;
        for (const record of records) {
          const element = record.target as HTMLElement;
          const id = element.dataset.entryId;
          if (!id || !element.isConnected) continue;
          const height = Math.ceil(record.borderBoxSize[0]?.blockSize ?? element.getBoundingClientRect().height);
          if (heights.set(id, width.current, height)) changed = true;
        }
        if (!changed) return;
        stickToBottom();
        cancelAnimationFrame(frame.current);
        frame.current = requestAnimationFrame(() => setVersion((value) => value + 1));
      }),
  );
  // Each row stops being observed as it leaves (observe's cleanup); only the pending redraw is left to cancel.
  useEffect(() => () => cancelAnimationFrame(frame.current), []);
  const observe = useCallback((element: HTMLDivElement | null) => {
    if (!element) return undefined;
    rows.observe(element);
    return () => rows.unobserve(element);
  }, [rows]);

  const empty = logs.length === 0 && requests.length === 0;
  useLayoutEffect(() => {
    const element = scroller.current;
    if (!element) return undefined;
    const fit = () => {
      const next = Math.round(element.clientWidth);
      if (next !== width.current) {
        width.current = next;
        // Heights are kept per width: the rows on screen are measured again at once, a row whose height did not change too.
        for (const row of element.querySelectorAll<HTMLElement>('[data-entry-id]')) heights.set(row.dataset.entryId!, next, Math.ceil(row.getBoundingClientRect().height));
      }
      setView((current) => (current.width === next && current.height === element.clientHeight ? current : { ...current, width: next, height: element.clientHeight }));
      stickToBottom();
    };
    fit();
    const resize = new ResizeObserver(fit);
    resize.observe(element);
    return () => resize.disconnect();
    // The scroller comes and goes with the empty state.
  }, [stickToBottom, empty, heights]);

  // An own send (natural submit B2): the view goes to the bottom, where its request is, whatever the position.
  const ownSeen = useRef(own);
  useLayoutEffect(() => {
    if (own === ownSeen.current) return;
    ownSeen.current = own;
    if (submitScroll('basic', true, true, following.current).scroll === 'toRequest') following.current = true;
  }, [own]);

  // After every drawing: a view at the bottom stays there as entries come and grow.
  useLayoutEffect(stickToBottom);

  // The requests and the running row under the entries grow without a draw here (전송 중 shows after its delay): kept in view.
  const [tail] = useState(() => new ResizeObserver(() => stickToBottom()));
  useEffect(() => () => tail.disconnect(), [tail]);
  const observeTail = useCallback((element: HTMLDivElement | null) => {
    tail.disconnect();
    if (element) tail.observe(element);
  }, [tail]);

  const offsets = useMemo(
    () => layout(logs, (entry: LogEntry) => heights.get(entry.id, view.width) ?? estimateHeight(entry)),
    // `version` changes when a measured height did.
    [logs, view.width, version, heights],
  );
  const [start, end] = visibleRange(offsets, view.top, view.height);
  const total = offsets[offsets.length - 1] ?? 0;

  const onScroll = useCallback(() => {
    const element = scroller.current;
    if (!element) return;
    following.current = nearBottom(element.scrollHeight, element.scrollTop, element.clientHeight);
    setView((current) => (current.top === element.scrollTop ? current : { ...current, top: element.scrollTop }));
    setMenu(null);
  }, []);

  const onToggle = useCallback((id: string) => {
    setExpanded((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }, []);

  const showReference = useContext(ReferenceContext);
  const follow = useCallback((clicked: Clicked) => {
    if (!clicked) return false;
    if ('url' in clicked) void bridge.call('openUrl', { url: clicked.url }).catch(() => {});
    else showReference?.(clicked);
    return true;
  }, [bridge, showReference]);
  const open = useCallback((element: Element) => follow(linkAt(element)), [follow]);

  const onClick = useCallback((event: MouseEvent<HTMLDivElement>) => {
    const target = event.target as Element;
    if (event.button !== 0 || target.closest('button') || !scroller.current) return;
    follow(clickedAt(target, event.clientX, event.clientY, scroller.current));
  }, [follow]);

  const onKeyDown = useCallback((event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key === 'Enter' && event.ctrlKey && open(event.target as Element)) event.preventDefault();
    if (event.key === 'Escape') setMenu(null);
  }, [open]);

  const onContextMenu = useCallback((event: MouseEvent<HTMLDivElement>) => {
    event.preventDefault();
    const code = (event.target as Element).closest('[data-code-block]')?.querySelector('[data-code]')?.textContent ?? null;
    setMenu({ x: event.clientX, y: event.clientY, selection: window.getSelection()?.toString() ?? '', code });
  }, []);

  useEffect(() => {
    if (!menu) return undefined;
    const close = () => setMenu(null);
    window.addEventListener('pointerdown', close);
    window.addEventListener('blur', close);
    return () => {
      window.removeEventListener('pointerdown', close);
      window.removeEventListener('blur', close);
    };
  }, [menu]);

  if (empty) return <EmptyPane session={session} />;
  const live = session.status === 'running';
  const shell = session.kind === 'shell';
  return (
    <div
      className="conversation"
      ref={scroller}
      data-testid="conversation"
      data-entry-count={logs.length}
      onScroll={onScroll}
      onClick={onClick}
      onKeyDown={onKeyDown}
      onContextMenu={onContextMenu}
    >
      <div className="conversation-body" style={{ paddingTop: offsets[start], paddingBottom: total - (offsets[end] ?? total) }}>
        {logs.slice(start, end).map((entry) => (
          <Row
            key={entry.id}
            observe={observe}
            entry={entry}
            paneId={session.id}
            provider={session.provider}
            shell={shell}
            live={live && !!entry.activity && (entry.activity.state === 'running' || entry.activity.state === 'waiting')}
            expanded={expanded.has(entry.id)}
            onToggle={onToggle}
          />
        ))}
      </div>
      {(requests.length > 0 || running) && (
        <div className="conversation-tail" ref={observeTail}>
          {running && <RunningRow activity={activity} />}
          {requests.map((request) => <PendingBubble key={request.clientId} request={request} />)}
        </div>
      )}
      {menu && (
        <div className="context-menu" role="menu" data-page-overlay="" style={{ left: menu.x, top: menu.y }} onPointerDown={(event) => event.stopPropagation()}>
          <button type="button" role="menuitem" disabled={menu.selection.length === 0} onClick={() => { void navigator.clipboard.writeText(menu.selection).catch(() => {}); setMenu(null); }}>
            {t('pane.copyButton')}
          </button>
          {menu.code !== null && (
            <button type="button" role="menuitem" onClick={() => { void navigator.clipboard.writeText(menu.code ?? '').catch(() => {}); setMenu(null); }}>
              {t('menu.copyCodeBlock')}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/** The empty pane (EmptyStates.cs): the agent's mark, "start working with" its name (with the beta capsule), and what to do. */
function EmptyPane({ session }: { session: RunSession }) {
  const { t } = useServices();
  if (session.kind !== 'claude' && session.kind !== 'shell') return <div className="conversation" data-testid="conversation" data-entry-count={0} />;
  const shell = session.kind === 'shell';
  const beta = !shell && (session.provider === 'codex' || session.provider === 'gemini');
  return (
    <div className="conversation" data-testid="conversation" data-entry-count={0}>
      <div className="pane-empty" data-testid="pane-empty">
        {shell ? <span className="pane-empty-shell" aria-hidden="true" /> : <ProviderMark provider={session.provider} size={24} />}
        <div className="pane-empty-title">
          <span>{shell ? t('pane.empty.shellTitle') : t('pane.empty.agentTitle', { provider: providerLabel(session.provider) })}</span>
          {beta && <span className="beta-badge">{t('badge.beta')}</span>}
        </div>
        <div className="pane-empty-body">{t(shell ? 'pane.empty.shellBody' : 'pane.empty.agentBody')}</div>
      </div>
    </div>
  );
}
