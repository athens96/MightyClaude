import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import en from '@locales/en.json';
import { ServicesContext, type AppServices } from '../app/context';
import { translator } from '../i18n/locale';
import { Questionnaire } from './Cards';
import { cardKey, forwardIndex, moveFocus, restoreIndex } from './keys';
import { answered, answers, back, canJump, canSubmit, emptyDraft, jump, next, pick, setText, toggleCustom, typing } from './questionnaire';
import type { Question } from './types';
import { reduce, emptyState } from '../store/store';

const single: Question = { header: 'Color', question: 'Which colour?', multiSelect: false, options: [{ label: 'Red', description: '' }, { label: 'Blue', description: '' }] };
const multiple: Question = { header: 'Sides', question: 'Which sides?', multiSelect: true, options: [{ label: 'Left', description: '' }, { label: 'Right', description: '' }] };
/** Another agent's questions: one with nothing to pick (a typed answer), one of twelve options, and eight to a card. */
const typedOnly: Question = { header: 'Name', question: 'What should the branch be called?', multiSelect: false, options: [] };
const twelve: Question = { header: 'Model', question: 'Which model?', multiSelect: false, options: Array.from({ length: 12 }, (_, i) => ({ label: 'Model ' + (i + 1), description: '' })) };
const eight: Question[] = Array.from({ length: 8 }, (_, i) => ({ ...single, question: `Colour ${i + 1}?` }));

describe('questionnaire draft', () => {
  it('a single question keeps one pick and drops its own answer', () => {
    let draft = toggleCustom(emptyDraft, single);
    draft = setText(draft, 'green');
    expect(answered(draft, [single], 0)).toBe(true);
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
    expect(answered(draft, [single], 0)).toBe(false);
    draft = setText(draft, '   ');
    expect(answered(draft, [single], 0)).toBe(false);
  });

  it('next, back, the dots and send follow the answered steps', () => {
    const questions = [single, multiple];
    let draft = next(emptyDraft, questions);
    expect(draft.step).toBe(0);
    expect(canJump(draft, questions, 1)).toBe(false);
    draft = pick(draft, single, 'Red');
    expect(canJump(draft, questions, 1)).toBe(true);
    draft = next(draft, questions);
    expect(draft.step).toBe(1);
    expect(next(draft, questions).step).toBe(1);
    expect(canSubmit(draft, questions)).toBe(false);
    draft = pick(draft, multiple, 'Left');
    expect(canSubmit(draft, questions)).toBe(true);
    expect(back(draft).step).toBe(0);
    expect(back(back(draft)).step).toBe(0);
    expect(jump(emptyDraft, questions, 1).step).toBe(0);
  });

  it('a question with nothing to pick is answered in words alone, which it needs to go on and to send', () => {
    const questions = [typedOnly, single];
    expect(typing(emptyDraft, questions, 0)).toBe(true);
    expect(typing(emptyDraft, questions, 1)).toBe(false);
    expect(answered(emptyDraft, questions, 0)).toBe(false);
    expect(next(emptyDraft, questions).step).toBe(0);
    let draft = setText(emptyDraft, '   ');
    expect(answered(draft, questions, 0)).toBe(false);
    draft = setText(draft, 'feature/agents');
    expect(answered(draft, questions, 0)).toBe(true);
    expect(canJump(draft, questions, 1)).toBe(true);
    draft = next(draft, questions);
    expect(draft.step).toBe(1);
    expect(canSubmit(draft, questions)).toBe(false);
    draft = pick(draft, single, 'Blue');
    expect(canSubmit(draft, questions)).toBe(true);
    expect(answers(draft, questions)).toEqual([{ picks: [], custom: 'feature/agents' }, { picks: ['Blue'], custom: null }]);
    expect(canSubmit(emptyDraft, [typedOnly])).toBe(false);
    expect(answers(setText(emptyDraft, 'x'), [{ ...typedOnly, multiSelect: true }])).toEqual([{ picks: [], custom: 'x' }]);
  });

  it('twelve options: the last is picked like the first, and several come back in the options order', () => {
    expect(answers(pick(emptyDraft, twelve, 'Model 12'), [twelve])).toEqual([{ picks: ['Model 12'], custom: null }]);
    const many: Question = { ...twelve, multiSelect: true };
    const draft = ['Model 11', 'Model 2', 'Model 7'].reduce((current, label) => pick(current, many, label), emptyDraft);
    expect(answers(draft, [many])).toEqual([{ picks: ['Model 2', 'Model 7', 'Model 11'], custom: null }]);
  });

  it('eight questions: each dot opens once the steps before it are answered, and Send waits for all eight', () => {
    let draft = emptyDraft;
    for (let step = 0; step < 8; step++) {
      expect(draft.step).toBe(step);
      expect(canJump(draft, eight, 7)).toBe(step === 7);
      expect(canSubmit(draft, eight)).toBe(false);
      draft = next(pick(draft, eight[step]!, step % 2 ? 'Blue' : 'Red'), eight);
    }
    expect(draft.step).toBe(7);
    expect(canSubmit(draft, eight)).toBe(true);
    expect(jump(draft, eight, 2).step).toBe(2);
    expect(answers(draft, eight).map((answer) => answer.picks[0])).toEqual(['Red', 'Blue', 'Red', 'Blue', 'Red', 'Blue', 'Red', 'Blue']);
  });
});

describe('question card', () => {
  const services = { bridge: { call: () => Promise.resolve(null) }, store: {}, t: translator([en]) } as unknown as AppServices;
  const draw = (questions: Question[]) => renderToStaticMarkup(createElement(ServicesContext.Provider, { value: services },
    createElement(Questionnaire, { card: { kind: 'questionnaire', requestId: 'r', count: 1, questions }, reply: () => Promise.resolve({ ok: true as const, value: null }) })));
  const count = (html: string, text: string) => html.split(text).length - 1;

  it('with nothing to pick, shows the typed answer at once as the only input, and Send waits for it', () => {
    const html = draw([typedOnly]);
    expect(html).toContain('What should the branch be called?');
    expect(html).not.toContain('question-choices');
    expect(html).not.toContain('data-answer="custom-0"');
    expect(html).not.toContain(en['phone.questionnaire.single']);
    expect(html).toMatch(/<textarea[^>]*data-answer="typed-0"[^>]*aria-required="true"/);
    expect(html).toMatch(/<button[^>]*data-answer="submit"[^>]*disabled=""/);
  });

  it('draws twelve options as tiles, then the own-answer tile, with the typed answer closed until it is chosen', () => {
    const html = draw([twelve]);
    expect(count(html, 'class="question-choice"')).toBe(13);
    expect(html).toContain('data-answer="option-0-11"');
    expect(html).toContain('>Model 12<');
    expect(html).toContain('data-answer="custom-0"');
    expect(html).toContain(en['phone.questionnaire.single']);
    expect(html).not.toContain('<textarea');
  });

  it('draws a dot for each of eight questions, the first one now and the others shut', () => {
    const html = draw(eight);
    expect(html).toContain('Question 1/8');
    expect(count(html, 'data-answer="step-')).toBe(8);
    expect(html).toContain('class="question-dot now"');
    expect(html).toMatch(/data-answer="step-7"[^>]*disabled=""/);
    expect(html).toContain('data-answer="next"');
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
    // A step with nothing to pick: its typed answer, not the first dot or Cancel.
    expect(restoreIndex(['step-0', 'step-1', 'typed-1', 'cancel', 'back', 'submit'], [true, true, true, true, true, false], 'next')).toBe(2);
    expect(restoreIndex(['typed-0', 'cancel', 'submit'], [true, true, false], null)).toBe(0);
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
