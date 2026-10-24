import { createContext, useCallback, useContext, type MouseEvent } from 'react';
import { useServices } from '../app/context';
import { textTargetAt, type ReferenceTarget } from './references';

interface Caret {
  node: Node;
  offset: number;
}

type CaretDocument = Document & {
  caretPositionFromPoint?: (x: number, y: number) => { offsetNode: Node; offset: number } | null;
  caretRangeFromPoint?: (x: number, y: number) => Range | null;
};

/** The text position under a point, by whichever of the two caret APIs the engine has. */
function caretAt(x: number, y: number): Caret | null {
  const doc = document as CaretDocument;
  const position = doc.caretPositionFromPoint?.(x, y);
  if (position) return { node: position.offsetNode, offset: position.offset };
  const range = doc.caretRangeFromPoint?.(x, y);
  return range ? { node: range.startContainer, offset: range.startOffset } : null;
}

/** What a link or a file name under an element names: a web address, a local file, or nothing. */
export type Clicked = { url: string } | ReferenceTarget | null;

/** A link the renderer drew (an address or a local destination) at or around `element`. */
export function linkAt(element: Element): Clicked {
  const url = element.closest<HTMLElement>('[data-url]')?.dataset.url;
  if (url) return { url };
  const path = element.closest<HTMLElement>('[data-path]')?.dataset.path;
  return path ? { path, line: null } : null;
}

/**
 * What a click at (x, y) on `target` names inside `within`: a drawn link, or the file named under the pointer
 * (ReferencePreview.TextTargetAt) read on its line of its text block (`[data-text]`). A drag that selected words is no click.
 */
export function clickedAt(target: Element, x: number, y: number, within: Element): Clicked {
  const link = linkAt(target);
  if (link) return link;
  const selection = window.getSelection();
  if (selection && !selection.isCollapsed) return null;
  const caret = caretAt(x, y);
  if (!caret) return null;
  const from = caret.node.nodeType === Node.TEXT_NODE ? caret.node.parentElement : (caret.node as Element);
  const block = from?.closest('[data-text]');
  if (!block || !within.contains(block)) return null;
  const range = document.createRange();
  range.setStart(block, 0);
  range.setEnd(caret.node, caret.offset);
  return textTargetAt(block.textContent ?? '', range.toString().length);
}

/**
 * Opens a file a pane names in the pane's reference bubble (src/pane/ReferenceBubble.tsx); null where no bubble is offered
 * (a remote pane, whose files are on its host, or outside a pane).
 */
export const ReferenceContext = createContext<((target: ReferenceTarget) => void) | null>(null);

/**
 * A click handler for a view that draws transcripts (a diagram block, a timeline row): a link opens in the browser, a file
 * named under the pointer in the pane's reference bubble. A click on a button, or where no bubble is offered, does nothing.
 */
export function useReferenceClick(): (event: MouseEvent<HTMLElement>) => void {
  const { bridge } = useServices();
  const show = useContext(ReferenceContext);
  return useCallback((event: MouseEvent<HTMLElement>) => {
    const target = event.target as Element;
    if (event.button !== 0 || target.closest('button')) return;
    const clicked = clickedAt(target, event.clientX, event.clientY, event.currentTarget);
    if (!clicked) return;
    if ('url' in clicked) void bridge.call('openUrl', { url: clicked.url }).catch(() => {});
    else show?.(clicked);
  }, [bridge, show]);
}
