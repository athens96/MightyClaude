import { describe, expect, it } from 'vitest';
import { isMarked, yesNoEmphasis } from './terminalPrompt';
import { emptyState, reduce } from '../store/store';

describe('terminal prompt card', () => {
  it('a choice line is marked by its pointer', () => {
    expect(isMarked('❯   preview')).toBe(true);
    expect(isMarked('  > production')).toBe(true);
    expect(isMarked('development')).toBe(false);
  });

  it('the default answer is named and highlighted, yes when there is none', () => {
    expect(yesNoEmphasis(true)).toEqual({ yesDefault: true, noDefault: false, yesAccent: true, noAccent: false });
    expect(yesNoEmphasis(false)).toEqual({ yesDefault: false, noDefault: true, yesAccent: false, noAccent: true });
    expect(yesNoEmphasis(undefined)).toEqual({ yesDefault: false, noDefault: false, yesAccent: true, noAccent: false });
  });

  it('the pane keeps the pushed card', () => {
    const card = { kind: 'terminalPrompt' as const, promptId: 'h1:40', text: 'Continue? (Y/n)', options: [], input: 'yesNo' as const, defaultYes: true };
    expect(reduce(emptyState, { kind: 'cards', paneId: 'a', cards: [card] }).cards['a']).toEqual([card]);
  });
});
