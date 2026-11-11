import { describe, expect, it } from 'vitest';
import { LogDiagramRoom, LogHeadHeight, LogRowsHeight, logRowsFit } from './flow';

describe('the session log strip in a short pane', () => {
  it('shows its rows only while the diagram keeps its own room beside them', () => {
    const tallest = LogHeadHeight + LogRowsHeight + LogDiagramRoom;
    expect(logRowsFit(tallest)).toBe(true);
    expect(logRowsFit(tallest + 400)).toBe(true);
    // A pane whose flow is no taller than the open strip: before, the strip kept its five rows and left the diagram 0 px.
    expect(logRowsFit(tallest - 1)).toBe(false);
    expect(logRowsFit(LogHeadHeight + LogRowsHeight)).toBe(false);
    expect(logRowsFit(120)).toBe(false);
  });

  it('shows them while the flow is not measured yet', () => {
    expect(logRowsFit(0)).toBe(true);
  });
});
