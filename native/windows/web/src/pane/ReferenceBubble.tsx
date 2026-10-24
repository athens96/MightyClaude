import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { Markdown } from '../conversation/MarkdownView';
import type { ReferenceTarget } from '../conversation/references';
import { ImageView, SourceView } from '../panes/files/FilesView';
import type { FilePreview } from '../panes/files/types';
import { BubbleTail, bubbleDrag, bubbleFrame, lineScrollTop, referenceName, referenceSymbol, tailTop, wantedBubbleSize, type BubbleSize } from './reference';

/** What `referencePreview` answers (MainWindow.WebShell.cs): null for a pane the page offers no bubble in. */
interface ReferenceAnswer {
  found: boolean;
  relativePath?: string;
  line?: number | null;
  preview?: FilePreview;
}

export interface ReferenceRequest extends ReferenceTarget {
  /** Counts the clicks, so the same file twice opens again. */
  seq: number;
}

/** The head's height (Layout.PreviewHead): the tail's middle stands at it. */
const Head = 30;

function HeadSymbol({ kind }: { kind: string }) {
  return (
    <svg className="reference-symbol" viewBox="0 0 14 14" aria-hidden="true">
      {kind === 'image' && <><rect x="1.5" y="2.5" width="11" height="9" rx="1" /><path d="M2.5 10.5l3-3 2 2 1.5-1.5 2.5 2.5" /></>}
      {kind === 'html' && <><circle cx="7" cy="7" r="5.5" /><path d="M1.5 7h11M7 1.5c-2 2-2 9 0 11M7 1.5c2 2 2 9 0 11" /></>}
      {kind === 'markdown' && <><path d="M3 1.5h5.5l2.5 2.5v8.5H3z" /><path d="M5 7h4M5 9h4" /></>}
      {kind === 'source' && <><path d="M3 1.5h5.5l2.5 2.5v8.5H3z" /><path d="M5 6h4M5 8h4M5 10h2" /></>}
      {kind === 'missing' && <><path d="M1.5 3.5h4l1.2 1.3h5.8v6.7h-11z" /><path d="M6 7.2a1 1 0 1 1 1.4.9c-.4.2-.4.5-.4.9M7 10.3v.1" /></>}
    </svg>
  );
}

function Notice({ text }: { text: string }) {
  return (
    <div className="reference-notice" data-testid="reference-notice">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 2.5h8.5l4 4v15H6z" /><path d="M10 11a2.2 2.2 0 1 1 3 2c-.8.4-.8 1-.8 1.8M12.2 17.6v.2" /></svg>
      <span>{text}</span>
    </div>
  );
}

/**
 * The reference bubble (WinUI MainWindow.ReferencePreview.cs, M/MightyGraphReferenceBubble.swift): a file a pane named, shown in
 * a speech bubble docked 12 inside the pane's content (under the Mighty bar while the Mighty view shows) on the right until
 * flipped, its tail and its resize grip on the side that faces the content. The head names the file (with the line asked
 * for) over its path, with flip, show in Explorer, open in its default app and close; the body is the files pane's preview
 * of it (source with line numbers scrolled to the line, rendered Markdown, a picture) or the reason there is none. The
 * host resolves the path inside the workspace (ReferencePreview.Resolve) and reads it (FilePreviewLoader); the size a drag
 * leaves is saved for every pane (ReferenceBubbleWidth/Height), a double click on the grip forgets it.
 */
export function ReferenceBubble({ paneId, request, top, onClose }: { paneId: string; request: ReferenceRequest; top: number; onClose: () => void }) {
  const { bridge, t } = useServices();
  const snapshot = useStoreState().snapshot;
  const room = useRef<HTMLDivElement>(null);
  const body = useRef<HTMLDivElement>(null);
  const [roomSize, setRoomSize] = useState({ width: 0, height: 0 });
  const [answer, setAnswer] = useState<ReferenceAnswer | 'loading' | 'failed'>('loading');
  const [onLeft, setOnLeft] = useState(false);
  const [live, setLive] = useState<BubbleSize | null>(null);
  const drag = useRef<{ x: number; y: number; start: { width: number; height: number }; moved: boolean } | null>(null);

  useLayoutEffect(() => {
    const element = room.current;
    if (!element) return;
    const measure = () => setRoomSize((s) => (s.width === element.clientWidth && s.height === element.clientHeight ? s : { width: element.clientWidth, height: element.clientHeight }));
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    let current = true;
    setAnswer('loading');
    bridge.call<ReferenceAnswer | null>('referencePreview', { paneId, path: request.path, line: request.line }).then((value) => {
      if (!current) return;
      if (!value) onClose(); else setAnswer(value);
    }, () => { if (current) setAnswer('failed'); });
    return () => { current = false; };
  }, [bridge, paneId, request, onClose]);

  // Escape closes it while the focus is in its pane.
  useEffect(() => {
    const key = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      const pane = room.current?.closest('.pane');
      if (drag.current) { drag.current = null; setLive(null); event.preventDefault(); return; }
      if (pane && (pane.contains(document.activeElement) || document.activeElement === document.body)) { onClose(); event.preventDefault(); }
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [onClose]);

  const found = typeof answer === 'object' && answer.found ? answer : null;
  const preview = found?.preview;
  const line = found ? found.line ?? null : request.line;
  // The source scrolled to the line asked for once it is drawn.
  useLayoutEffect(() => {
    const source = body.current?.querySelector<HTMLElement>('.files-source');
    if (!source || !line) return;
    const height = parseFloat(getComputedStyle(source).lineHeight) || 17;
    source.scrollTop = lineScrollTop(line, height);
  }, [preview, line]);

  const wanted = live ?? wantedBubbleSize(snapshot?.referenceBubbleWidth, snapshot?.referenceBubbleHeight);
  const frame = bubbleFrame(roomSize.width, roomSize.height, wanted);

  const save = (size: BubbleSize | null) => void bridge.call('referenceSize', size ? { width: size.width, height: size.height } : {}).catch(() => {});
  const onGripDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    drag.current = { x: event.clientX, y: event.clientY, start: frame, moved: false };
  };
  const onGripMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d) return;
    d.moved = true;
    setLive(bubbleDrag(d.start, event.clientX - d.x, event.clientY - d.y, onLeft));
  };
  const onGripUp = () => {
    const d = drag.current;
    drag.current = null;
    // Released, the size is every pane's (clamped as drawn); a press that moved nothing saves nothing.
    if (d?.moved && live) save(bubbleFrame(roomSize.width, roomSize.height, live));
    setLive(null);
  };

  const name = referenceName(request.path);
  const title = line ? t('reference.line', { title: name, line }) : name;
  const symbol = answer === 'loading' || answer === 'failed' || found ? referenceSymbol(request.path) : 'missing';
  const markdownRendered = preview?.kind === 'markdown' && preview.markdown;
  const banners = preview && !preview.failure ? (markdownRendered ? preview.renderedBanners ?? [] : preview.banners) : [];
  let content;
  if (answer === 'loading') content = <div className="files-progress" />;
  else if (answer === 'failed') content = <Notice text={t('files.preview.failed')} />;
  else if (!found || !preview) content = <Notice text={t('files.preview.missing')} />;
  else if (preview.failure) content = <Notice text={preview.failure} />;
  else if (markdownRendered) content = <div className="files-markdown"><Markdown text={preview.text ?? ''} /></div>;
  else if (preview.kind === 'source' || preview.kind === 'markdown') content = <SourceView preview={preview} />;
  else if (preview.kind === 'image' && preview.image) content = <ImageView preview={preview} zoom={null} />;
  else content = <Notice text={preview.reason ?? t('files.preview.unsupported')} />;

  const relative = found?.relativePath;
  const act = (method: string) => { if (relative) void bridge.call(method, { paneId, path: relative }).catch(() => {}); };
  const side = onLeft ? 'left' : 'right';
  return (
    <div className="reference-room" ref={room} style={{ top }}>
      <div className={'reference-bubble on-' + side} role="dialog" aria-label={t('reference.title')} data-testid="reference-bubble"
        data-reference-path={request.path} data-reference-line={line ?? ''} data-reference-kind={answer === 'loading' ? 'loading' : !found ? 'missing' : preview?.failure ? 'failed' : preview?.kind ?? ''}
        style={{ width: frame.width + BubbleTail, height: frame.height }}>
        <svg className="reference-tail" width={BubbleTail + 2} height={18} style={{ top: tailTop(frame.height, Head) }} aria-hidden="true">
          <path className="reference-tail-fill" d={`M${BubbleTail + 2} 0.5L0.7 9L${BubbleTail + 2} 17.5Z`} />
          <path className="reference-tail-edge" d={`M${BubbleTail + 0.5} 0L0.5 9L${BubbleTail + 0.5} 18`} />
        </svg>
        <div className="reference-card">
          <div className="reference-head">
            <HeadSymbol kind={symbol} />
            <div className="reference-names">
              <span className="reference-title">{title}</span>
              <span className="reference-path" title={request.path}>{request.path}</span>
            </div>
            <div className="reference-tools">
              <button type="button" className="files-tool" data-testid="reference-flip" aria-label={t(onLeft ? 'reference.flipRight' : 'reference.flipLeft')} title={t(onLeft ? 'reference.flipRight' : 'reference.flipLeft')} onClick={() => setOnLeft((v) => !v)}>
                <svg viewBox="0 0 14 14" aria-hidden="true">{onLeft ? <path d="M1.5 2.5h11v9h-11zM9 2.5v9M4 7h3M5.5 5.5 7 7 5.5 8.5" /> : <path d="M1.5 2.5h11v9h-11zM5 2.5v9M10 7H7M8.5 5.5 7 7 8.5 8.5" />}</svg>
              </button>
              {relative && (
                <>
                  <button type="button" className="files-tool" data-testid="reference-reveal" aria-label={t('menu.showInExplorer')} title={t('menu.showInExplorer')} onClick={() => act('referenceReveal')}>
                    <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M1.5 3.5h4l1.2 1.3h5.8v6.7h-11z" /></svg>
                  </button>
                  <button type="button" className="files-tool" data-testid="reference-open" aria-label={t('reference.openExternal')} title={t('reference.openExternal')} onClick={() => act('referenceOpen')}>
                    <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M8 2h4v4M12 2 6.5 7.5M10.5 8.5V12h-8.5V3.5H5.5" /></svg>
                  </button>
                </>
              )}
              <button type="button" className="files-tool" data-testid="reference-close" aria-label={t('reference.close')} title={t('reference.close')} onClick={onClose}>
                <svg viewBox="0 0 14 14" aria-hidden="true"><path d="M3 3l8 8M11 3l-8 8" /></svg>
              </button>
            </div>
          </div>
          {banners.map((banner) => <div key={banner} className="files-banner">{banner}</div>)}
          <div className="reference-body" ref={body}>{content}</div>
          <div className="reference-grip" role="separator" aria-label={t('reference.resize')} title={t('reference.resizeHelp')}
            onPointerDown={onGripDown} onPointerMove={onGripMove} onPointerUp={onGripUp} onPointerCancel={() => { drag.current = null; setLive(null); }}
            onDoubleClick={() => { setLive(null); save(null); }}>
            <svg viewBox="0 0 10 10" aria-hidden="true"><path d="M1.5 4.5v-3h3M1.5 1.5l3 3M8.5 5.5v3h-3M8.5 8.5l-3-3" /></svg>
          </div>
        </div>
      </div>
    </div>
  );
}
