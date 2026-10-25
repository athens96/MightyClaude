import { describe, expect, it } from 'vitest';
import { cardKey, forwardIndex, moveFocus, restoreIndex } from './keys';
import { answered, answers, back, canJump, canSubmit, emptyDraft, jump, next, pick, setText, toggleCustom } from './questionnaire';
import type { Question } from './types';
import { reduce, emptyState } from '../store/store';

const single: Question = { header: 'Color', question: 'Which colour?', multiSelect: false, options: [{ label: 'Red', description: '' }, { label: 'Blue', description: '' }] };
const multiple: Question = { header: 'Sides', question: 'Which sides?', multiSelect: true, options: [{ label: 'Left', description: '' }, { label: 'Right', description: '' }] };

describe('questionnaire draft', () => {
  it('a single question keeps one pick and drops its own answer', () => {
    let draft = toggleCustom(emptyDraft, single);
    draft = setText(draft, 'green');
    expect(answered(draft, 0)).toBe(true);
    draft = pick(draft, single, 'Red');
    draft = pick(draft, single, 'Blue');
    expect(draft.picks[0]).toEqual(['Blue']);
    expect(draft.custom[0]).toBe(false);
    expect(answers(draft, [single])).toEqual([{ picks: ['Blue'], custom: null }]);
  });

  it('a multiple question toggles picks and keeps them in the options order beside its own words', () => {
    let draft = pick(emptyDraft, multiple, 'Right');
    draft = pick(draft, multiple, 'Left');
    draft = toggleCustom(draft, multiple);
    draft = setText(draft, 'top');
    expect(answers(draft, [multiple])).toEqual([{ picks: ['Left', 'Right'], custom: 'top' }]);
    draft = pick(draft, multiple, 'Left');
    expect(draft.picks[0]).toEqual(['Right']);
  });

  it('own answer without words does not answer the step; turning it on drops a single pick', () => {
    let draft = pick(emptyDraft, single, 'Red');
    draft = toggleCustom(draft, single);
    expect(draft.picks[0]).toEqual([]);
    expect(answered(draft, 0)).toBe(false);
    draft = setText(draft, '   ');
    expect(answered(draft, 0)).toBe(false);
  });

  it('next, back, the dots and send follow the answered steps', () => {
    let draft = next(emptyDraft, 2);
    expect(draft.step).toBe(0);
    expect(canJump(draft, 1)).toBe(false);
    draft = pick(draft, single, 'Red');
    expect(canJump(draft, 1)).toBe(true);
    draft = next(draft, 2);
    expect(draft.step).toBe(1);
    expect(next(draft, 2).step).toBe(1);
    expect(canSubmit(draft, 2)).toBe(false);
    draft = pick(draft, multiple, 'Left');
    expect(canSubmit(draft, 2)).toBe(true);
    expect(back(draft).step).toBe(0);
    expect(back(back(draft)).step).toBe(0);
    expect(jump(emptyDraft, 1).step).toBe(0);
  });
});

describe('card keyboard', () => {
  it('arrows move without wrapping, Home and End go to the ends, other keys are not the card\'s', () => {
    expect(moveFocus('ArrowRight', 0, 3)).toBe(1);
    expect(moveFocus('ArrowDown', 2, 3)).toBe(2);
    expect(moveFocus('ArrowLeft', 0, 3)).toBe(0);
    expect(moveFocus('ArrowUp', 2, 3)).toBe(1);
    expect(moveFocus('Home', 2, 3)).toBe(0);
    expect(moveFocus('End', 0, 3)).toBe(2);
    expect(moveFocus('Enter', 0, 3)).toBeNull();
    expect(moveFocus('ArrowDown', 0, 0)).toBeNull();
  });

  it('focus comes back to the same answer, else the first option, else the first enabled button', () => {
    const ids = ['cancel', 'option-0-0', 'option-0-1', 'submit'];
    expect(restoreIndex(ids, [true, true, true, true], 'submit')).toBe(3);
    expect(restoreIndex(ids, [true, true, true, false], 'submit')).toBe(1);
    expect(restoreIndex(ids, [true, false, false, true], 'gone')).toBe(0);
    expect(restoreIndex(['deny', 'allow'], [false, false], null)).toBe(-1);
  });

  it('a press on the card goes to the chosen answer before the first one', () => {
    const ids = ['step-0', 'option-0-0', 'option-0-1', 'custom-0', 'cancel', 'next'];
    const on = [true, true, true, true, true, true];
    expect(restoreIndex(ids, on, null, [false, false, true, false, false, false])).toBe(2);
    expect(restoreIndex(ids, on, null, [false, false, false, false, false, false])).toBe(1);
    expect(restoreIndex(ids, [true, true, false, true, true, true], null, [false, false, true, false, false, false])).toBe(1);
    expect(restoreIndex(ids, on, 'cancel', [false, false, true, false, false, false])).toBe(4);
    // Next turned into Send: the same answer is gone, so the step's chosen or first answer takes the keyboard.
    expect(restoreIndex(['option-1-0', 'option-1-1', 'submit'], [true, true, false], 'next')).toBe(0);
  });

  it('Enter picks a single question\'s answer and goes on; Space, buttons and several answers keep their own press', () => {
    expect(cardKey('Enter', 'radio', 1, 4)).toEqual({ kind: 'pickAndGoOn' });
    expect(cardKey(' ', 'radio', 1, 4)).toBeNull();
    expect(cardKey('Enter', 'checkbox', 1, 4)).toBeNull();
    expect(cardKey(' ', 'checkbox', 1, 4)).toBeNull();
    expect(cardKey('Enter', null, 3, 4)).toBeNull();
    expect(cardKey('Tab', 'radio', 1, 4)).toBeNull();
    expect(cardKey('ArrowDown', 'radio', 1, 4)).toEqual({ kind: 'move', index: 2 });
    expect(cardKey('End', null, 0, 4)).toEqual({ kind: 'move', index: 3 });
    expect(cardKey('ArrowDown', 'radio', 1, 4, true)).toBeNull();
    expect(cardKey('Enter', 'radio', 1, 4, true)).toBeNull();
  });

  it('the forward button is Next or Send when it can be pressed', () => {
    expect(forwardIndex(['option-0-0', 'cancel', 'next'], [true, true, true])).toBe(2);
    expect(forwardIndex(['option-1-0', 'cancel', 'back', 'submit'], [true, true, true, true])).toBe(3);
    expect(forwardIndex(['option-0-0', 'cancel', 'next'], [true, true, false])).toBe(-1);
    expect(forwardIndex(['deny', 'allow'], [true, true])).toBe(-1);
  });
});

describe('cards in the store', () => {
  it('a cards push replaces the pane\'s cards and a fill is kept for the composer', () => {
    let state = reduce(emptyState, { kind: 'cards', paneId: 'a', cards: [{ kind: 'webOpen', requestId: 'r', url: 'https://example.com/' }] });
    expect(state.cards.a).toHaveLength(1);
    state = reduce(state, { kind: 'fill', paneId: 'a', text: 'do it' });
    state = reduce(state, { kind: 'fill', paneId: 'a', text: 'do it' });
    expect(state.fills.a).toEqual({ text: 'do it', seq: 2 });
  });
});
