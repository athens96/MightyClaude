// The popup standard's one dialog (.omc/plans/popup-standard.md, kinds A, B and C): the page's own sheets (Dialogs.tsx), the
// host's WinUI dialogs drawn by the page (native/Surfaces.tsx), the plan document and the picture viewer all open in it.
//   A alert  — 400 wide, buttons only, Esc = cancel.            B sheet — S 360 · M 460 · L 560, buttons only, Esc = cancel.
//   C panel  — ✕ at the header's end (Esc the same; dimmed and refused while busy), a corner grip, the header's double-click fits
//              the window and back, a least size, and the last size kept per kind of panel (panelSize.ts).
// Header 48 (icon 20, title 15 semibold over a 12 subtitle, tools at the end), a body that alone scrolls, and a 52 footer with
// the status at the left and the buttons at the right ([secondary] [cancel] [primary]), left out when it holds neither.
import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type HTMLAttributes, type PointerEvent, type ReactNode, type Ref } from 'react';
import tokens from '@tokens/design-tokens.json';
import { useServices } from '../app/context';
import { Icon } from '../icons/Icon';
import { clampSize, dragSize, fitSize, loadSize, saveSize, sheetWidth, type Size } from './panelSize';

export type DialogKind = 'alert' | 'sheet' | 'panel';

const metrics = tokens.metrics.dialog;
/** A panel's least size when its form names none. */
export const PanelMinimum: Size = { width: 420, height: 320 };

/** A form sheet's width for what the form asked (S, M or L). */
export const formSheetWidth = (asked?: number | null) => sheetWidth(asked, { s: metrics.sheetWidthS, m: metrics.sheetWidthM, l: metrics.sheetWidthL });

/** The frame's header double-click: fits the window, then gives the size back. */
const FitContext = createContext<(() => void) | null>(null);

function useRoom(): Size {
  const [room, setRoom] = useState<Size>(() => typeof window === 'undefined' ? { width: 1280, height: 800 } : { width: window.innerWidth, height: window.innerHeight });
  useEffect(() => {
    const resize = () => setRoom({ width: window.innerWidth, height: window.innerHeight });
    window.addEventListener('resize', resize);
    return () => window.removeEventListener('resize', resize);
  }, []);
  return room;
}

export interface DialogFrameProps {
  kind: DialogKind;
  /** The sheet's asked width (snapped to S/M/L) or the panel's default; the alert is always 400. */
  width?: number | null;
  /** The panel's default height (a sheet's fixed one, if any). */
  height?: number | null;
  minWidth?: number | null;
  minHeight?: number | null;
  /** The kind of panel whose last size is kept (plugins, plan, pictures, settings…). */
  panel?: string;
  label: string;
  /** A panel that cannot close now: Esc is ignored and its ✕ is dimmed. */
  busy?: boolean;
  /** Only the top dialog takes Esc and the focus. */
  top?: boolean;
  onCancel: () => void;
  /** Where focus goes as it opens: its `data-autofocus` control (else the first control), or the frame itself. */
  focus?: 'content' | 'frame' | 'none';
  /** The backdrop's own attributes and handlers (test ids, data-*, a press beside the card). */
  backdrop?: Record<string, unknown>;
  /** Keys inside the dialog after its own Esc (a focus trap). */
  onKeyDown?: (event: KeyboardEvent) => void;
  /** The backdrop's stacking: `surface` under the dialogs of the window's flows. */
  layer?: 'dialog' | 'surface';
  testId?: string;
  className?: string;
  style?: CSSProperties;
  frameRef?: Ref<HTMLDivElement>;
  children: ReactNode;
}

/** The backdrop and the card: sizing, resizing, Esc and focus. Settings draws its own two panes inside it. */
export function DialogFrame(props: DialogFrameProps) {
  const { kind, panel, busy = false, top = true, onCancel, focus = 'content' } = props;
  const { t } = useServices();
  const room = useRoom();
  const element = useRef<HTMLDivElement | null>(null);
  const defaults: Size = { width: props.width ?? 640, height: props.height ?? 480 };
  const least: Size = { width: props.minWidth ?? Math.min(PanelMinimum.width, defaults.width), height: props.minHeight ?? Math.min(PanelMinimum.height, defaults.height) };
  const [size, setSize] = useState<Size | null>(() => kind === 'panel' && panel ? loadSize(panel) : null);
  const [fitted, setFitted] = useState(false);
  const shown = kind === 'panel' ? clampSize(fitted ? fitSize(room) : size ?? defaults, least, room) : null;
  const shownRef = useRef(shown);
  shownRef.current = shown;

  useLayoutEffect(() => {
    if (!top || focus === 'none') return;
    const root = element.current;
    if (!root) return;
    // In this order: the form's own choice, its first field, the footer's primary, else the card itself (never a header tool).
    const target = focus === 'frame' ? root
      : ['[data-autofocus]', '.dialog-body :is(input, select, textarea):not(:disabled)', '.dialog-footer .btn.primary:not(:disabled)', '.dialog-footer button:not(:disabled)']
        .map((selector) => root.querySelector<HTMLElement>(selector)).find(Boolean) ?? root;
    target?.focus({ preventScroll: true });
  }, [top, focus]);

  const cancel = useRef(onCancel);
  cancel.current = onCancel;
  const key = (event: KeyboardEvent) => {
    props.onKeyDown?.(event);
    if (event.key !== 'Escape' || !top || event.nativeEvent.isComposing || event.defaultPrevented) return;
    event.preventDefault(); event.stopPropagation();
    if (kind === 'panel' && busy) return;
    cancel.current();
  };

  const keep = useCallback((next: Size) => { setSize(next); setFitted(false); if (panel) saveSize(panel, next); }, [panel]);
  const toggleFit = useCallback(() => { if (kind === 'panel') setFitted((now) => !now); }, [kind]);
  const drag = useRef<{ x: number; y: number; start: Size } | null>(null);
  const down = (event: PointerEvent<HTMLElement>) => {
    if (event.button !== 0 || !shownRef.current) return;
    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    drag.current = { x: event.clientX, y: event.clientY, start: shownRef.current };
  };
  const move = (event: PointerEvent<HTMLElement>) => {
    const start = drag.current;
    if (start) { setSize(dragSize(start.start, event.clientX - start.x, event.clientY - start.y, least, room)); setFitted(false); }
  };
  const up = () => { if (drag.current && shownRef.current) keep(shownRef.current); drag.current = null; };
  const gripKey = (event: KeyboardEvent<HTMLElement>) => {
    const step = event.shiftKey ? 64 : 16;
    const by = { ArrowRight: [step, 0], ArrowLeft: [-step, 0], ArrowDown: [0, step], ArrowUp: [0, -step] }[event.key];
    if (!by || !shownRef.current) return;
    event.preventDefault();
    keep(clampSize({ width: shownRef.current.width + by[0]!, height: shownRef.current.height + by[1]! }, least, room));
  };

  const style: CSSProperties = { ...props.style };
  if (kind === 'alert') style.width = metrics.alertWidth;
  else if (kind === 'sheet') { style.width = formSheetWidth(props.width); if (props.height) style.height = props.height; }
  else if (shown) { style.width = shown.width; style.height = shown.height; }
  return (
    <div className={'dialog-backdrop' + (props.layer === 'surface' ? ' surface' : '')} {...(props.backdrop as HTMLAttributes<HTMLDivElement>)} onKeyDown={key}>
      <div className={`dialog kind-${kind}` + (props.className ? ' ' + props.className : '')} role="dialog" aria-modal="true" aria-label={props.label} data-testid={props.testId}
        tabIndex={-1} style={style}
        ref={(node) => { element.current = node; const forward = props.frameRef; if (typeof forward === 'function') forward(node); else if (forward) (forward as { current: HTMLDivElement | null }).current = node; }}>
        <FitContext.Provider value={kind === 'panel' ? toggleFit : null}>{props.children}</FitContext.Provider>
        {kind === 'panel' && (
          <span className="dialog-grip" role="button" tabIndex={0} aria-label={t('dialog.resize')} title={t('dialog.resize')} data-testid="dialog-grip"
            onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={up} onKeyDown={gripKey} onDoubleClick={toggleFit}>
            <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M9 1 1 9M9 5 5 9" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>
          </span>
        )}
      </div>
    </div>
  );
}

/** A header's double-click target: fits a panel to the window and back (nothing for the other kinds). */
export function useFitToggle(): (() => void) | undefined {
  return useContext(FitContext) ?? undefined;
}

export interface DialogHeaderProps {
  title: ReactNode;
  subtitle?: ReactNode;
  icon?: ReactNode;
  tools?: ReactNode;
  /** The ✕ (panels only): closes, or while busy stays dimmed with the reason. */
  onClose?: () => void;
  busy?: boolean;
  closeTestId?: string;
}

export function DialogHeader({ title, subtitle, icon, tools, onClose, busy = false, closeTestId }: DialogHeaderProps) {
  const fit = useFitToggle();
  return (
    <header className="dialog-header" onDoubleClick={(event) => { if (fit && !(event.target as Element).closest('button, input, select, a')) fit(); }}>
      {icon && <span className="dialog-icon" aria-hidden="true">{icon}</span>}
      <div className="dialog-heading">
        <strong className="dialog-title">{title}</strong>
        {subtitle && <span className="dialog-subtitle">{subtitle}</span>}
      </div>
      {(tools || onClose) && (
        <div className="dialog-tools">
          {tools}
          {onClose && <CloseButton onClose={onClose} busy={busy} testId={closeTestId} />}
        </div>
      )}
    </header>
  );
}

/** The panel's ✕ (28 hit, 14 glyph, radius 8): dimmed with `dialog.closeBusy` while the panel cannot close. */
export function CloseButton({ onClose, busy = false, testId = 'dialog-close' }: { onClose: () => void; busy?: boolean; testId?: string }) {
  const { t } = useServices();
  const label = busy ? t('dialog.closeBusy') : t('dialog.close');
  return (
    <button type="button" className={'dialog-close' + (busy ? ' busy' : '')} aria-label={t('dialog.close')} aria-disabled={busy || undefined} title={label} data-testid={testId}
      onClick={() => { if (!busy) onClose(); }}>
      <Icon name="x" size={metrics.closeGlyph} weight={2} />
    </button>
  );
}

/** The footer (52): the status at the left, the buttons at the right; nothing when it holds neither. */
export function DialogFooter({ status, children }: { status?: ReactNode; children?: ReactNode }) {
  const holds = (node: ReactNode): boolean => Array.isArray(node) ? node.some(holds) : node !== null && node !== undefined && node !== false && node !== '';
  if (!holds(status) && !holds(children)) return null;
  return (
    <footer className="dialog-footer">
      <div className="dialog-status">{status}</div>
      {children}
    </footer>
  );
}

/** The centred wait of a body that is being read (`dialog.loading`), with three row-shaped places under it for a list. */
export function DialogLoading({ rows = 3 }: { rows?: number }) {
  const { t } = useServices();
  return (
    <div className="dialog-loading" role="status" data-testid="dialog-loading">
      <span className="dialog-loading-line"><span className="dialog-spinner" aria-hidden="true" />{t('dialog.loading')}</span>
      {Array.from({ length: rows }, (_, index) => <span key={index} className="dialog-skeleton" aria-hidden="true"><i /><i /><i /></span>)}
    </div>
  );
}

export interface DialogShellProps extends Omit<DialogFrameProps, 'children'>, Omit<DialogHeaderProps, 'onClose'> {
  /** The ✕ in the header: panels have it, alerts and sheets do not. */
  closable?: boolean;
  status?: ReactNode;
  buttons?: ReactNode;
  bodyClassName?: string;
  bodyRef?: Ref<HTMLDivElement>;
  children?: ReactNode;
}

/** The whole dialog: frame, header, the scrolling body and the footer. */
export function DialogShell({ closable, status, buttons, bodyClassName, bodyRef, children, title, subtitle, icon, tools, closeTestId, ...frame }: DialogShellProps) {
  const closes = closable ?? frame.kind === 'panel';
  return (
    <DialogFrame {...frame}>
      <DialogHeader title={title} subtitle={subtitle} icon={icon} tools={tools} onClose={closes ? frame.onCancel : undefined} busy={frame.busy} closeTestId={closeTestId} />
      <div className={'dialog-body' + (bodyClassName ? ' ' + bodyClassName : '')} ref={bodyRef}>{children}</div>
      <DialogFooter status={status}>{buttons}</DialogFooter>
    </DialogFrame>
  );
}
