// The picture viewer over the window: the WinUI viewer's dialog (MainWindow.TranscriptImages.cs OpenTranscriptImage) and the
// Mac's (M/MightyGraphImagesView.swift's open, the transcript's image viewer) drawn by the page. The picture fits the room,
// − / 맞춤 / + (and ctrl+wheel, a touchpad's pinch) zoom it between 25% and 400%, a drag moves it while it is larger than the
// room; 이미지 복사, 저장, 탐색기에서 보기 and 기본 앱으로 열기 are the host's (`imageAction`, PaneView.TranscriptImageAction).
// Esc or a click beside the card closes it; focus stays inside while it is open and goes back where it was.
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { useServices, useStoreState } from '../app/context';
import { Icon, type IconName } from '../icons/Icon';
import { usePageOverlay } from '../shell/overlay';
import { clampPan, clampZoom, drawnSize, stepZoom, zoomPercent, ZoomMax, ZoomMin, type OpenPicture, type Offset, type PictureAction, type Size } from './viewer';

/** The viewer while a picture is open; nothing otherwise. Mounted once, in the shell. */
export function PictureViewer() {
  const picture = useStoreState().picture;
  return picture ? <Viewer key={picture.url + JSON.stringify(picture.source)} picture={picture} /> : null;
}

const actions: { action: PictureAction; label: string; icon: IconName }[] = [
  { action: 'copy', label: 'images.copy', icon: 'copy' },
  { action: 'save', label: 'images.save', icon: 'download' },
  { action: 'reveal', label: 'menu.showInExplorer', icon: 'folder-open' },
  { action: 'openDefault', label: 'images.openDefault', icon: 'external-link' },
];

/** The open viewer itself; exported for its markup test. */
export function Viewer({ picture }: { picture: OpenPicture }) {
  const { bridge, store, t } = useServices();
  const card = useRef<HTMLDivElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const [room, setRoom] = useState<Size>({ width: 0, height: 0 });
  const [natural, setNatural] = useState<Size | null>(null);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState<Offset>({ x: 0, y: 0 });
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const drag = useRef<{ x: number; y: number; pan: Offset } | null>(null);
  usePageOverlay(true);
  const close = () => store.dispatch({ kind: 'picture' });
  const drawn = natural ? drawnSize(natural, room, zoom) : null;
  const zoomTo = (next: number) => { setZoom(next); setPan((current) => natural ? clampPan(current, drawnSize(natural, room, next), room) : current); };

  // Focus goes in at once and back to what held it (the thumbnail or tile) when the viewer closes.
  useEffect(() => {
    const before = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    card.current?.querySelector<HTMLElement>('[data-autofocus]')?.focus();
    return () => { if (before?.isConnected) before.focus(); };
  }, []);
  useLayoutEffect(() => {
    const element = stage.current;
    if (!element) return undefined;
    const measure = () => setRoom({ width: element.clientWidth, height: element.clientHeight });
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  // Ctrl+wheel, which a touchpad's pinch also sends, zooms the picture rather than the page (a passive React wheel cannot stop it).
  const zoomRef = useRef(zoomTo);
  zoomRef.current = zoomTo;
  const zoomNow = useRef(zoom);
  zoomNow.current = zoom;
  useEffect(() => {
    const element = stage.current;
    if (!element) return undefined;
    const wheel = (event: WheelEvent) => {
      if (!event.ctrlKey) return;
      event.preventDefault();
      zoomRef.current(clampZoom(zoomNow.current * Math.exp(-event.deltaY / 300)));
    };
    element.addEventListener('wheel', wheel, { passive: false });
    return () => element.removeEventListener('wheel', wheel);
  }, []);

  // Esc closes wherever focus is, before the window's own keys see it.
  useEffect(() => {
    const escape = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); store.dispatch({ kind: 'picture' }); } };
    window.addEventListener('keydown', escape, true);
    return () => window.removeEventListener('keydown', escape, true);
  }, [store]);

  const key = (event: KeyboardEvent) => {
    if (event.key !== 'Tab' || !card.current) return;
    // Tab and Shift+Tab go round the card's controls.
    const focusable = [...card.current.querySelectorAll<HTMLElement>('button:not(:disabled), [tabindex="0"]')];
    if (focusable.length === 0) return;
    const first = focusable[0]!, last = focusable[focusable.length - 1]!;
    if (event.shiftKey && (document.activeElement === first || !card.current.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  };
  const pannable = drawn !== null && (drawn.width > room.width + 0.5 || drawn.height > room.height + 0.5);
  const down = (event: PointerEvent<HTMLDivElement>) => {
    if (!pannable || event.button !== 0) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    drag.current = { x: event.clientX, y: event.clientY, pan };
  };
  const move = (event: PointerEvent<HTMLDivElement>) => {
    const start = drag.current;
    if (!start || !drawn) return;
    setPan(clampPan({ x: start.pan.x + event.clientX - start.x, y: start.pan.y + event.clientY - start.y }, drawn, room));
  };
  const act = (action: PictureAction) => {
    setBusy(true); setFailure(null);
    void bridge.call<string | null>('imageAction', { ...picture.source, action })
      .then((value) => setFailure(value ?? null), (error: unknown) => setFailure(String(error instanceof Error ? error.message : error)))
      .finally(() => setBusy(false));
  };
  return (
    <div className="picture-backdrop" data-page-overlay="" data-testid="picture-viewer" onClick={(event) => { if (event.target === event.currentTarget) close(); }}>
      <div className="picture-card" ref={card} role="dialog" aria-modal="true" aria-label={t('images.open')} onKeyDown={key}>
        <div className="picture-head">
          <span className="picture-title">{t('images.open')}</span>
          {picture.alt && <span className="picture-name" title={picture.alt}>{picture.alt}</span>}
          <span className="spacer" />
          <button type="button" className="icon-button" data-testid="picture-close" aria-label={t('settings.closeButton')} title={t('settings.closeButton')} onClick={close}><Icon name="x" size={15} weight={2} /></button>
        </div>
        <div ref={stage} className={'picture-stage' + (pannable ? ' pannable' : '')}
          onPointerDown={down} onPointerMove={move} onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }}
          onDoubleClick={() => { setZoom(1); setPan({ x: 0, y: 0 }); }}>
          <img src={picture.url} alt={picture.alt} draggable={false} data-testid="picture-viewer-image"
            onLoad={(event) => setNatural({ width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight })}
            style={drawn ? { width: drawn.width, height: drawn.height, transform: `translate(${pan.x}px, ${pan.y}px)` } : { visibility: 'hidden' }} />
        </div>
        <div className="picture-foot">
          <button type="button" className="icon-button" aria-label={t('files.image.zoomOut')} title={t('files.image.zoomOut')} disabled={zoom <= ZoomMin} onClick={() => zoomTo(stepZoom(zoom, -1))}><Icon name="zoom-out" size={15} weight={1.75} /></button>
          <button type="button" className="plain-button" data-autofocus onClick={() => { setZoom(1); setPan({ x: 0, y: 0 }); }}>{t('files.image.fit')}</button>
          <button type="button" className="icon-button" aria-label={t('files.image.zoomIn')} title={t('files.image.zoomIn')} disabled={zoom >= ZoomMax} onClick={() => zoomTo(stepZoom(zoom, 1))}><Icon name="zoom-in" size={15} weight={1.75} /></button>
          <span className="picture-zoom" data-testid="picture-zoom">{zoomPercent(zoom)}</span>
          <span className="spacer" />
          {actions.map((item) => (
            <button key={item.action} type="button" className="plain-button picture-action" data-testid={'picture-' + item.action} disabled={busy} onClick={() => act(item.action)}>
              <Icon name={item.icon} size={13} weight={1.75} />{t(item.label)}
            </button>
          ))}
        </div>
        {failure && <div className="picture-failure tone-text-err" role="alert">{failure}</div>}
      </div>
    </div>
  );
}
