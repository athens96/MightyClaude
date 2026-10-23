// A card's keyboard (MainWindow.ToolPermission.cs WaitCard): once the keyboard is in a card the arrow keys move between its
// answers and buttons, without wrapping, as XY focus does; Home and End go to the first and the last. Space and Enter press
// the button in focus (the button's own behaviour). Pure: which answer is next, or null when the key is not the card's.

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

/**
 * Where the keyboard goes after a card is drawn again: the answer with the same id when it is still there and enabled, else
 * the question's first answer tile, else the card's first button (RestoreCardFocus); -1 when none can take it.
 */
export function restoreIndex(ids: readonly string[], enabled: readonly boolean[], previous: string | null): number {
  if (previous !== null) {
    const same = ids.indexOf(previous);
    if (same >= 0 && enabled[same]) return same;
  }
  const option = ids.findIndex((id, index) => id.startsWith('option-') && enabled[index]);
  if (option >= 0) return option;
  return enabled.indexOf(true);
}
