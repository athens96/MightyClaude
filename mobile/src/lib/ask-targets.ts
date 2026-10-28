// The composer's "send to" switch (docs/mobile-remote.md, native/contracts/fixtures/ask-targets.json). A host sends `askTargets`
// and `askTarget` on a Claude pane that offers other CLIs through oh-my-claudecode's ask; the host wraps the text, so the phone
// only shows the choices and sends the id. Pure.
import type { AskTargetOption } from '@/api/types';

/** The picker's id for Claude itself, which is no target the host lists. */
export const CLAUDE_ASK = 'claude';

export interface AskTargetsView {
  targets: AskTargetOption[];
  /** The target sends go to now; null is Claude. */
  selected: string | null;
}

/** The well-formed targets of a detail, and the selected one when it is among them. Anything else reads as no switch / Claude. */
export function parseAskTargets(detail: { askTargets?: unknown; askTarget?: unknown } | undefined): AskTargetsView {
  const raw = detail?.askTargets;
  const targets: AskTargetOption[] = [];
  if (Array.isArray(raw)) {
    for (const entry of raw as unknown[]) {
      if (typeof entry !== 'object' || entry === null) continue;
      const { id, label } = entry as { id?: unknown; label?: unknown };
      if (typeof id !== 'string' || id === '' || typeof label !== 'string' || label === '') continue;
      if (targets.some((target) => target.id === id)) continue;
      targets.push({ id, label });
    }
  }
  const chosen = detail?.askTarget;
  const selected = typeof chosen === 'string' && targets.some((target) => target.id === chosen) ? chosen : null;
  return { targets, selected };
}

/**
 * The choice to show: the one just made, held only while the detail it was made against is still the latest (the next
 * detail carries the host's own word), else the host's.
 */
export function shownAskTarget(
  view: AskTargetsView,
  choice: { target: string | null; revision: number | undefined } | undefined,
  revision: number | undefined,
): string | null {
  if (!choice || choice.revision !== revision) return view.selected;
  return choice.target === null || view.targets.some((target) => target.id === choice.target) ? choice.target : view.selected;
}

/** Whether staged files go with a send: only to Claude. Asking another CLI keeps them staged and sends text alone. */
export function filesTravel(target: string | null): boolean {
  return target === null;
}

/** The composer's "nothing to send" rule: files count only while they travel, so a CLI asked with files alone has no draft. */
export function draftIsEmpty(text: string, fileCount: number, target: string | null): boolean {
  return text.trim().length === 0 && (!filesTravel(target) || fileCount === 0);
}
