// A card's keyboard (MainWindow.ToolPermission.cs WaitCard): once the keyboard is in a card the arrow keys move between its
// answers and buttons, without wrapping, as XY focus does; Home and End go to the first and the last. Space and Enter press
// the button in focus (the button's own behaviour), except Enter on a single question's answer, which picks it and goes on
// to the forward button, so Enter, Enter answers. Pure: which answer is next, or null when the key is not the card's.

export function moveFocus(key: string, index: number, count: number): number | null {
  if (count === 0) return null;
  switch (key) {
    case 'ArrowRight':
    case 'ArrowDown':
      return Math.min(index + 1, count - 1);
    case 'ArrowLeft':
    case 'ArrowUp':
      return Math.max(index - 1, 0);
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    default:
      return null;
  }
}

/** What a key does on one of a card's answers: move to another, pick a single question's answer and go on, or nothing of the card's. */
export type CardKey = { kind: 'move'; index: number } | { kind: 'pickAndGoOn' } | null;

/**
 * A key on the answer at `index` of the card's `count` enabled ones; `role` is the answer's (radio: a single question's answer).
 * With Alt, Ctrl or Meta the key is not the card's; Tab never is. Enter and Space elsewhere are the button's own press (a
 * multiple question's answer toggles with either).
 */
export function cardKey(key: string, role: string | null, index: number, count: number, modified = false): CardKey {
  if (modified) return null;
  if (key === 'Enter') return role === 'radio' ? { kind: 'pickAndGoOn' } : null;
  const next = moveFocus(key, index, count);
  return next === null ? null : { kind: 'move', index: next };
}

/** The question card's forward button (Next, or Send on the last step) when it can be pressed; -1 otherwise. */
export function forwardIndex(ids: readonly string[], enabled: readonly boolean[]): number {
  return ids.findIndex((id, index) => (id === 'next' || id === 'submit') && enabled[index]);
}

/**
 * Where the keyboard goes after a card is drawn again, or when the card itself is pressed: the answer with the same id when
 * it is still there and enabled, else the chosen answer, else the question's first answer tile, else the card's first button
 * (RestoreCardFocus); -1 when none can take it.
 */
export function restoreIndex(ids: readonly string[], enabled: readonly boolean[], previous: string | null, checked: readonly boolean[] = []): number {
  if (previous !== null) {
    const same = ids.indexOf(previous);
    if (same >= 0 && enabled[same]) return same;
  }
  const chosen = ids.findIndex((_, index) => checked[index] === true && enabled[index]);
  if (chosen >= 0) return chosen;
  const option = ids.findIndex((id, index) => id.startsWith('option-') && enabled[index]);
  if (option >= 0) return option;
  return enabled.indexOf(true);
}
