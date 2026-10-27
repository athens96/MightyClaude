// Draws a WinUI form's node tree (types.ts) and sends every input back to the control it was read from.
import { createContext, useContext, useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { boxStyle, edges, ink, nodeText, type MenuItem, type NativeNode as Node } from './types';
import { Pending } from '../pending/Pending';

/** What a node's input does: `nativeAction` on the view it belongs to. */
export type Act = (id: number, op: 'invoke' | 'toggle' | 'select' | 'text' | 'value', value?: unknown) => void;

export const ActContext = createContext<Act>(() => {});

/** True inside Settings' form, where a labelled picker row is laid out as the Mac's (the label at its width, the picker filling the rest). */
export const SettingsFormContext = createContext(false);

/**
 * A row of Settings that is a label and a picker (SettingsLabeled with a segmented control or a pop-up): the Mac's LabeledContent
 * gives the label its own width and the picker the rest of the row (M/SettingsViews.swift), where the WinUI form keeps a share or
 * the picker's own width at the right. Null for any other grid.
 */
const fixedColumn = (track: string | undefined) => !!track && /^\d+(\.\d+)?px$/.test(track.trim());
const sized = (n: Node): boolean => n.w !== undefined || ((n.t === 'grid' || n.t === 'box' || n.t === 'stack') && n.c.length === 1 && sized(n.c[0]!));
const framedSelect = (n: Node): boolean => n.t === 'select' || ((n.t === 'grid' || n.t === 'box' || n.t === 'stack') && n.c.length === 1 && framedSelect(n.c[0]!));

/** The children of a Settings grid that fill their cell: a pop-up in a column of fixed width takes all of it, as the Mac's do. */
export function fillsFixedCell(node: Extract<Node, { t: 'grid' }>, child: Node): boolean {
  return framedSelect(child) && !sized(child) && fixedColumn(node.cols?.[child.col ?? 0]);
}

export function labelledPickerRow(node: Extract<Node, { t: 'grid' }>): number | null {
  if (node.cols?.length !== 2 || node.c.length !== 2) return null;
  const control = node.c.findIndex((child) => (child.col ?? 0) === 1);
  const label = node.c.findIndex((child) => (child.col ?? 0) === 0);
  if (control < 0 || label < 0) return null;
  const picker = node.c[control]!;
  // A picker the form gives a column of its own width keeps it, as the Mac's fixed-width pickers do (the phase models' 220).
  if (fixedColumn(node.cols[1]) || sized(picker)) return null;
  const pickerLike = picker.t === 'segmented' || framedSelect(picker) || (picker.t === 'grid' && picker.c.length > 1 && picker.c.every((c) => c.t === 'button' || c.t === 'box' || c.t === 'rule'));
  return pickerLike && node.c[label]!.t === 'text' ? control : null;
}

function Menu({ node }: { node: Extract<Node, { t: 'menu' }> }) {
  const act = useContext(ActContext);
  const [open, setOpen] = useState(false);
  const element = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (!open) return;
    const away = (event: PointerEvent) => { if (!element.current?.contains(event.target as globalThis.Node)) setOpen(false); };
    window.addEventListener('pointerdown', away);
    return () => window.removeEventListener('pointerdown', away);
  }, [open]);
  const rows = (items: MenuItem[]): ReactNode => items.map((item, index) => item.sep ? <div key={index} className="native-menu-sep" />
    : item.items ? <div key={index} className="native-menu-group"><div className="native-menu-heading">{item.label}</div>{rows(item.items)}</div>
    : (
      <button key={index} type="button" role="menuitem" className="native-menu-row" disabled={item.disabled}
        onClick={() => { setOpen(false); if (item.id !== undefined) act(item.id, 'invoke'); }}>
        <span className="native-menu-check">{item.checked ? '✓' : ''}</span>{item.label}
      </button>
    ));
  return (
    <span className="native-menu-host" ref={element}>
      <button type="button" className="native-button native-pull" aria-haspopup="menu" aria-expanded={open} aria-label={node.name || undefined} disabled={node.disabled}
        data-native-id={node.id} onClick={() => setOpen(!open)}>
        {node.label ?? (node.c ?? []).map((child, i) => <NativeNode key={i} node={child} />)}
        <span className="native-chevron" aria-hidden="true">⌄</span>
      </button>
      {open && <div className="native-menu" role="menu">{rows(node.items)}</div>}
    </span>
  );
}

function Field({ node }: { node: Extract<Node, { t: 'field' }> }) {
  const act = useContext(ActContext);
  const [text, setText] = useState(node.text);
  const focused = useRef(false);
  // The host's words win while the field is not being typed in (a section that fills it, a reset).
  useEffect(() => { if (!focused.current) setText(node.text); }, [node.text]);
  const change = (value: string) => { setText(value); act(node.id, 'text', value); };
  const common = {
    className: 'native-field' + (node.mono ? ' mono' : ''), value: text, placeholder: node.placeholder ?? undefined, readOnly: node.readonly, disabled: node.disabled,
    'aria-label': node.name || node.placeholder || undefined, 'data-native-id': node.id,
    onFocus: () => { focused.current = true; }, onBlur: () => { focused.current = false; setText(node.text); },
  };
  return node.multiline
    ? <textarea {...common} rows={Math.min(12, Math.max(3, text.split('\n').length))} onChange={(event) => change(event.target.value)} />
    : <input {...common} type={node.password ? 'password' : 'text'} onChange={(event) => change(event.target.value)} />;
}

function Children({ nodes }: { nodes: readonly Node[] | null | undefined }) {
  return <>{(nodes ?? []).map((child, index) => <NativeNode key={index} node={child} />)}</>;
}

function Inner({ node }: { node: Node }) {
  const act = useContext(ActContext);
  const settingsForm = useContext(SettingsFormContext);
  switch (node.t) {
    case 'text': {
      const style: CSSProperties = { fontSize: node.size ? `calc(${node.size}px * var(--text-scale, 1))` : undefined, color: ink(node.ink), fontWeight: node.weight === 'bold' ? 600 : node.weight === 'medium' ? 500 : undefined };
      return <span className={'native-text' + (node.mono ? ' mono' : '') + (node.nowrap ? ' nowrap' : '') + (node.selectable ? ' selectable' : '')} style={style}>{node.text}</span>;
    }
    case 'glyph':
      return <span className="native-glyph" aria-hidden="true" style={{ fontSize: node.size ? `calc(${node.size}px * var(--text-scale, 1))` : undefined, color: ink(node.ink) }}>{node.glyph}</span>;
    case 'image':
      return <img className="native-image" src={node.src} alt={node.help ?? ''} />;
    case 'dot':
      return <span className="native-dot" style={{ width: node.size, height: node.size, background: ink(node.fill) }} />;
    case 'spinner':
      return <Pending busy showsLabel={false} />;
    case 'progress':
      return node.indeterminate ? <progress className="native-progress" /> : <progress className="native-progress" value={node.value} max={node.max || 1} />;
    case 'rule':
      return <span className={'native-rule' + (node.vertical ? ' vertical' : '')} style={{ background: ink(node.fill) }} />;
    case 'link':
      return <button type="button" className="link-button" disabled={node.disabled} data-native-id={node.id} onClick={() => act(node.id, 'invoke')}>{node.label}</button>;
    case 'check':
      return <label className="native-check"><input type="checkbox" checked={node.on} disabled={node.disabled} data-native-id={node.id} onChange={() => act(node.id, 'toggle')} /> {node.label}</label>;
    case 'radio':
      return <label className="native-check"><input type="radio" checked={node.on} disabled={node.disabled} data-native-id={node.id} onChange={() => act(node.id, 'toggle')} /> {node.label}</label>;
    case 'switch':
      return (
        <button type="button" role="switch" aria-checked={node.on} aria-label={node.name || undefined} className={'native-switch' + (node.on ? ' on' : '')} disabled={node.disabled}
          data-native-id={node.id} onClick={() => act(node.id, 'toggle')}><span className="native-knob" /></button>
      );
    case 'segmented':
      return (
        <span className="native-segmented" role="radiogroup" aria-label={node.name || undefined}>
          {node.options.map((option) => (
            <button key={option.id} type="button" role="radio" aria-checked={option.on} className={option.on ? 'on' : ''} disabled={node.disabled} data-native-id={option.id}
              onClick={() => { if (!option.on) act(option.id, 'toggle'); }}>{option.label}</button>
          ))}
        </span>
      );
    case 'select':
      return (
        <select className="native-select" value={node.index} aria-label={node.name || undefined} disabled={node.disabled} data-native-id={node.id}
          onChange={(event) => act(node.id, 'select', Number(event.target.value))}>
          {node.index < 0 && <option value={-1} />}
          {node.items.map((item, index) => <option key={index} value={index}>{item}</option>)}
        </select>
      );
    case 'field':
      return <Field node={node} />;
    case 'slider':
      return <input type="range" className="native-slider" min={node.min} max={node.max} step={node.step || 'any'} value={node.value} aria-label={node.name || undefined} disabled={node.disabled}
        data-native-id={node.id} onChange={(event) => act(node.id, 'value', Number(event.target.value))} />;
    case 'button': {
      const plain = node.c !== undefined && node.label === undefined;
      return (
        <button type="button" className={plain ? 'native-plain' : 'native-button' + (node.accent ? ' accent' : '') + (node.destructive ? ' destructive' : '')} disabled={node.disabled}
          aria-label={node.name || undefined} title={node.help ?? (plain && !nodeText(node) ? node.name : undefined)} data-native-id={node.id} onClick={() => act(node.id, 'invoke')}>
          {node.label ?? <Children nodes={node.c} />}
        </button>
      );
    }
    case 'menu':
      return <Menu node={node} />;
    case 'expander':
      return (
        <div className="native-expander">
          <button type="button" className="native-plain" aria-expanded={node.open} data-native-id={node.id} onClick={() => act(node.id, 'toggle')}>
            <span aria-hidden="true">{node.open ? '▾' : '▸'}</span> {node.header && <NativeNode node={node.header} />}
          </button>
          {node.open && <Children nodes={node.c} />}
        </div>
      );
    case 'info':
      return <div className={'native-info ' + node.severity}>{node.title && <strong>{node.title}</strong>} {node.message}</div>;
    case 'list':
      return (
        <div className="native-list" role="listbox">
          {node.items.map((item, index) => (
            <div key={index} role="option" aria-selected={index === node.index} className={index === node.index ? 'selected' : ''} data-native-id={node.id} onClick={() => act(node.id, 'select', index)}>
              <NativeNode node={item} />
            </div>
          ))}
        </div>
      );
    case 'stack': {
      const style: CSSProperties = { gap: node.gap, padding: node.pad ? edges(node.pad) : undefined, background: ink(node.fill) };
      return <div className={'native-stack ' + node.dir} style={style}><Children nodes={node.c} /></div>;
    }
    case 'grid': {
      const fill = settingsForm ? labelledPickerRow(node) : null;
      const style: CSSProperties = {
        gridTemplateColumns: fill !== null ? 'auto minmax(0, 1fr)' : node.cols?.join(' '), gridTemplateRows: node.rows?.join(' '), rowGap: node.gap[0],
        columnGap: fill !== null ? 'var(--space-md)' : node.gap[1], padding: node.pad ? edges(node.pad) : undefined, background: ink(node.fill),
      };
      return <div className="native-grid" style={style}>{node.c.map((child, index) => <NativeNode key={index} node={child} fill={index === fill || (settingsForm && fillsFixedCell(node, child))} />)}</div>;
    }
    case 'box': {
      const style: CSSProperties = {
        background: ink(node.fill), padding: node.pad ? edges(node.pad) : undefined, borderRadius: node.radius,
        borderStyle: node.line ? 'solid' : undefined, borderColor: node.line ? ink(node.lineInk) ?? 'var(--line)' : undefined,
        borderWidth: node.line ? edges(node.line) : undefined,
      };
      return <div className="native-box" style={style}><Children nodes={node.c} /></div>;
    }
  }
}

/** One node with its placement (margin, alignment, size, grid cell). */
export function NativeNode({ node, fill = false }: { node: Node; fill?: boolean }) {
  const style = boxStyle(node) as CSSProperties;
  // A picker filling its labelled row (labelledPickerRow): the whole cell, whatever width or side the WinUI control kept.
  if (fill) { style.width = undefined; style.maxWidth = undefined; style.justifySelf = 'stretch'; style.alignSelf = 'center'; }
  return (
    <div className={'native-node native-n-' + node.t + (fill ? ' native-fill' : '')} style={style} title={node.t === 'button' ? undefined : node.help} data-aid={node.aid}>
      <Inner node={node} />
    </div>
  );
}
