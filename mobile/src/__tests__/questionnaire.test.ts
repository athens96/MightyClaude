import type { Question } from '@/api/types';
import {
  CUSTOM_TEXT_LIMIT,
  buildAnswers,
  canGoBack,
  canGoNext,
  canGoTo,
  createQuestionnaireState,
  currentQuestion,
  goBack,
  goNext,
  goTo,
  isAnswered,
  isComplete,
  isLastQuestion,
  isValid,
  pickFor,
  progressLabel,
  selectionHint,
  setCustomText,
  toggleCustom,
  toggleOption,
} from '@/lib/questionnaire';
import { resetLanguage } from '@/lib/i18n';

beforeAll(() => resetLanguage('ko'));
afterAll(() => resetLanguage());

function question(overrides: Partial<Question> & { question: string }): Question {
  return {
    header: '선택',
    multiSelect: false,
    options: [
      { label: '예', description: '그대로 진행' },
      { label: '아니오', description: '멈춤' },
    ],
    ...overrides,
  };
}

const one = question({ question: '계속할까요?' });
const two = question({
  question: '무엇을 먼저 할까요?',
  multiSelect: true,
  options: [
    { label: '첫째', description: '먼저' },
    { label: '둘째', description: '그 다음' },
    { label: '셋째', description: '마지막' },
  ],
});
const three = question({ question: '끝나면 알릴까요?' });
const questions = [one, two, three];

describe('moving through the questions', () => {
  it('starts on the first question with a 1/N progress label', () => {
    const state = createQuestionnaireState();
    expect(currentQuestion(questions, state)).toBe(one);
    expect(progressLabel(questions, state)).toBe('질문 1/3');
    expect(canGoBack(state)).toBe(false);
  });

  it('keeps 다음 disabled until the question on screen is answered', () => {
    let state = createQuestionnaireState();
    expect(canGoNext(questions, state)).toBe(false);
    state = toggleOption(state, one, '예');
    expect(canGoNext(questions, state)).toBe(true);
    state = goNext(questions, state);
    expect(progressLabel(questions, state)).toBe('질문 2/3');
  });

  it('refuses to move on when only whitespace was typed', () => {
    const state = setCustomText(createQuestionnaireState(), one, '   ');
    expect(isAnswered(state, one)).toBe(false);
    expect(canGoNext(questions, state)).toBe(false);
    expect(goNext(questions, state)).toBe(state);
  });

  it('accepts free text as an answer on its own', () => {
    const state = setCustomText(createQuestionnaireState(), one, '조금 더 생각해 볼게요');
    expect(isAnswered(state, one)).toBe(true);
    expect(canGoNext(questions, state)).toBe(true);
  });

  it('has no 다음 on the last question', () => {
    let state = goTo(questions, createQuestionnaireState(), 2);
    state = toggleOption(state, three, '예');
    expect(isLastQuestion(questions, state)).toBe(true);
    expect(canGoNext(questions, state)).toBe(false);
  });

  it('keeps picks in both directions and lets an earlier answer be changed', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = goNext(questions, state);
    state = toggleOption(state, two, '첫째');
    state = goBack(state);
    expect(currentQuestion(questions, state)).toBe(one);
    expect(pickFor(state, one).selectedOptions).toEqual(['예']);
    state = toggleOption(state, one, '아니오');
    expect(pickFor(state, one).selectedOptions).toEqual(['아니오']);
    state = goNext(questions, state);
    expect(pickFor(state, two).selectedOptions).toEqual(['첫째']);
  });

  it('clamps a stored index against a shorter question list', () => {
    const state = goTo(questions, createQuestionnaireState(), 9);
    expect(currentQuestion(questions, state)).toBe(three);
    expect(currentQuestion([one], state)).toBe(one);
    expect(progressLabel([one], state)).toBe('질문 1/1');
  });
});

describe('choosing options', () => {
  it('replaces the choice when only one is allowed', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = toggleOption(state, one, '아니오');
    expect(pickFor(state, one).selectedOptions).toEqual(['아니오']);
  });

  it('keeps the single selected option picked when it is tapped again, as the Mac does', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = toggleOption(state, one, '예');
    expect(pickFor(state, one).selectedOptions).toEqual(['예']);
    expect(isAnswered(state, one)).toBe(true);
  });

  it('adds and removes for a multi-select question, keeping the tap order', () => {
    let state = toggleOption(createQuestionnaireState(), two, '첫째');
    state = toggleOption(state, two, '둘째');
    state = toggleOption(state, two, '셋째');
    expect(pickFor(state, two).selectedOptions).toEqual(['첫째', '둘째', '셋째']);
    state = toggleOption(state, two, '둘째');
    expect(pickFor(state, two).selectedOptions).toEqual(['첫째', '셋째']);
  });
});

describe('an option or free text, never both, when only one may be chosen', () => {
  it('turns the 직접 입력 row off as soon as an option is picked, keeping its text unsent', () => {
    let state = setCustomText(createQuestionnaireState(), one, '조금 더 생각해 볼게요');
    state = toggleOption(state, one, '예');
    expect(pickFor(state, one)).toEqual({
      selectedOptions: ['예'],
      customText: '조금 더 생각해 볼게요',
      customChosen: false,
    });
    expect(isAnswered(state, one)).toBe(true);
    expect(buildAnswers([one], state)).toEqual({ '계속할까요?': { selectedOptions: ['예'] } });
  });

  it('drops the picked option as soon as free text is typed', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = setCustomText(state, one, '다르게 해 주세요');
    expect(pickFor(state, one)).toEqual({
      selectedOptions: [],
      customText: '다르게 해 주세요',
      customChosen: true,
    });
    expect(isAnswered(state, one)).toBe(true);
  });

  it('leaves the pick alone while the free text is still blank', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = setCustomText(state, one, '   ');
    expect(pickFor(state, one).selectedOptions).toEqual(['예']);
    expect(isAnswered(state, one)).toBe(true);
  });

  it('keeps options and free text together for a multi-select question', () => {
    let state = toggleOption(createQuestionnaireState(), two, '첫째');
    state = setCustomText(state, two, '그리고 정리도');
    state = toggleOption(state, two, '둘째');
    expect(pickFor(state, two)).toEqual({
      selectedOptions: ['첫째', '둘째'],
      customText: '그리고 정리도',
      customChosen: true,
    });
    expect(isAnswered(state, two)).toBe(true);
  });

  it('never lets 다음 or 답변 보내기 light up on a pick the host would reject', () => {
    // The two cannot be reached through the card any more, but a stale pick could still
    // hold both — the buttons stay dark rather than sending something rejected.
    const both = {
      picks: { [one.question]: { selectedOptions: ['예'], customText: '빠르게', customChosen: true } },
    };
    expect(isAnswered({ index: 0, ...both }, one)).toBe(false);
    expect(canGoNext(questions, { index: 0, ...both })).toBe(false);
    expect(isComplete([one], { index: 0, ...both })).toBe(false);
  });
});

describe('isValid', () => {
  it('mirrors the host: exactly one of an option and free text for a single pick', () => {
    expect(isValid(one, { selectedOptions: ['예'], customText: '', customChosen: false })).toBe(true);
    expect(isValid(one, { selectedOptions: [], customText: '따로 할게요', customChosen: true })).toBe(true);
    expect(isValid(one, { selectedOptions: ['예'], customText: '빠르게', customChosen: true })).toBe(false);
    expect(isValid(one, { selectedOptions: ['예', '아니오'], customText: '', customChosen: false })).toBe(false);
  });

  it('wants something chosen, whichever kind of question it is', () => {
    expect(isValid(one, { selectedOptions: [], customText: '  ', customChosen: true })).toBe(false);
    expect(isValid(two, { selectedOptions: [], customText: '', customChosen: false })).toBe(false);
  });

  it('counts free text only while the 직접 입력 row is chosen', () => {
    expect(isValid(one, { selectedOptions: [], customText: '따로 할게요', customChosen: false })).toBe(false);
    expect(isValid(one, { selectedOptions: ['예'], customText: '빠르게', customChosen: false })).toBe(true);
  });

  it('lets a multi-select question carry several options and free text at once', () => {
    expect(isValid(two, { selectedOptions: ['첫째', '셋째'], customText: '그리고', customChosen: true })).toBe(true);
    expect(isValid(two, { selectedOptions: ['첫째', '셋째'], customText: '', customChosen: false })).toBe(true);
  });

  it('refuses a repeat and a label the question never offered', () => {
    expect(isValid(two, { selectedOptions: ['첫째', '첫째'], customText: '', customChosen: false })).toBe(false);
    expect(isValid(two, { selectedOptions: ['넷째'], customText: '', customChosen: false })).toBe(false);
  });
});

describe('the 직접 입력 row', () => {
  it('takes the place of the single pick when chosen, and waits for text', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = toggleCustom(state, one);
    expect(pickFor(state, one)).toEqual({ selectedOptions: [], customText: '', customChosen: true });
    expect(isAnswered(state, one)).toBe(false);
    state = setCustomText(state, one, '나중에');
    expect(isAnswered(state, one)).toBe(true);
    expect(buildAnswers([one], state)).toEqual({
      '계속할까요?': { selectedOptions: [], customText: '나중에' },
    });
  });

  it('keeps the options of a multi-select question when chosen', () => {
    let state = toggleOption(createQuestionnaireState(), two, '둘째');
    state = toggleCustom(state, two);
    expect(pickFor(state, two)).toEqual({ selectedOptions: ['둘째'], customText: '', customChosen: true });
    expect(isAnswered(state, two)).toBe(true);
  });

  it('keeps its text when turned off, but no longer sends it', () => {
    let state = setCustomText(createQuestionnaireState(), two, '그리고 정리도');
    state = toggleOption(state, two, '첫째');
    state = toggleCustom(state, two);
    expect(pickFor(state, two).customText).toBe('그리고 정리도');
    expect(buildAnswers([two], state)).toEqual({ '무엇을 먼저 할까요?': { selectedOptions: ['첫째'] } });
    state = toggleCustom(state, two);
    expect(buildAnswers([two], state)).toEqual({
      '무엇을 먼저 할까요?': { selectedOptions: ['첫째'], customText: '그리고 정리도' },
    });
  });

  it('says how many choices may be taken, in the Mac wording', () => {
    expect(selectionHint(one)).toBe('하나 선택');
    expect(selectionHint(two)).toBe('여러 개 선택 가능');
  });
});

describe('canGoTo', () => {
  it('always allows a jump back and refuses one past an unanswered question', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = goNext(questions, state);
    expect(canGoTo(questions, state, 0)).toBe(true);
    expect(canGoTo(questions, state, 1)).toBe(true);
    expect(canGoTo(questions, state, 2)).toBe(false);
    state = toggleOption(state, two, '첫째');
    expect(canGoTo(questions, state, 2)).toBe(true);
    expect(currentQuestion(questions, goTo(questions, state, 2))).toBe(three);
  });

  it('has nowhere to jump without questions', () => {
    expect(canGoTo([], createQuestionnaireState(), 0)).toBe(false);
  });
});

describe('the completed answer map', () => {
  it('waits for every question before the send button lights up', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    expect(isComplete(questions, state)).toBe(false);
    state = toggleOption(state, two, '첫째');
    expect(isComplete(questions, state)).toBe(false);
    state = setCustomText(state, three, '알려 주세요');
    expect(isComplete(questions, state)).toBe(true);
  });

  it('is keyed by question text, with customText only when it is not blank', () => {
    // Typing over a single pick replaces it, so the map carries the text alone — which
    // is the only shape the host accepts for a question that takes one answer.
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = setCustomText(state, one, '  빠르게  ');
    state = toggleOption(state, two, '첫째');
    state = toggleOption(state, two, '둘째');
    state = setCustomText(state, three, '   ');
    state = toggleOption(state, three, '아니오');
    expect(buildAnswers(questions, state)).toEqual({
      '계속할까요?': { selectedOptions: [], customText: '빠르게' },
      '무엇을 먼저 할까요?': { selectedOptions: ['첫째', '둘째'] },
      '끝나면 알릴까요?': { selectedOptions: ['아니오'] },
    });
    expect(isComplete(questions, state)).toBe(true);
  });

  it('sends options in the order the question lists them, as the Mac does', () => {
    let state = toggleOption(createQuestionnaireState(), two, '셋째');
    state = toggleOption(state, two, '첫째');
    expect(pickFor(state, two).selectedOptions).toEqual(['셋째', '첫째']);
    expect(buildAnswers([two], state)).toEqual({ '무엇을 먼저 할까요?': { selectedOptions: ['첫째', '셋째'] } });
  });

  it('never leaves a question out of the map, even an untouched one', () => {
    const answers = buildAnswers(questions, createQuestionnaireState());
    expect(Object.keys(answers)).toEqual([
      '계속할까요?',
      '무엇을 먼저 할까요?',
      '끝나면 알릴까요?',
    ]);
    expect(answers['계속할까요?']).toEqual({ selectedOptions: [] });
  });

  it('has nothing to complete when the host sent no questions', () => {
    expect(isComplete([], createQuestionnaireState())).toBe(false);
    expect(currentQuestion([], createQuestionnaireState())).toBeUndefined();
  });
});

describe('typed answers the host would refuse', () => {
  it('takes text up to the limit in UTF-8 bytes, not characters', () => {
    const atLimit = setCustomText(createQuestionnaireState(), one, 'a'.repeat(CUSTOM_TEXT_LIMIT));
    expect(isAnswered(atLimit, one)).toBe(true);
    const overLimit = setCustomText(createQuestionnaireState(), one, 'a'.repeat(CUSTOM_TEXT_LIMIT + 1));
    expect(isAnswered(overLimit, one)).toBe(false);
    // 한 is three bytes: 2730 of them fit (8190 bytes), 2731 do not (8193), far short of 8192 characters.
    expect(isAnswered(setCustomText(createQuestionnaireState(), one, '한'.repeat(2730)), one)).toBe(true);
    expect(isAnswered(setCustomText(createQuestionnaireState(), one, '한'.repeat(2731)), one)).toBe(false);
    // An emoji outside the BMP is four bytes, not the two UTF-16 units it takes in JS.
    expect(isAnswered(setCustomText(createQuestionnaireState(), one, '😀'.repeat(2048)), one)).toBe(true);
    expect(isAnswered(setCustomText(createQuestionnaireState(), one, `${'😀'.repeat(2048)}a`), one)).toBe(false);
  });

  it('measures the text that is sent, after trimming', () => {
    const padded = setCustomText(createQuestionnaireState(), one, `  ${'a'.repeat(CUSTOM_TEXT_LIMIT)}  `);
    expect(isAnswered(padded, one)).toBe(true);
  });

  it('allows tab, line feed and carriage return but no other control character', () => {
    expect(isAnswered(setCustomText(createQuestionnaireState(), one, '첫 줄\n둘\t째\r\n끝'), one)).toBe(true);
    for (const control of ['\u0000', '\u0007', '\u000B', '\u000C', '\u001B', '\u007F', '\u0085', '\u009F']) {
      const state = setCustomText(createQuestionnaireState(), one, `앞${control}뒤`);
      expect(isAnswered(state, one)).toBe(false);
    }
  });

  it('holds a multi-select answer back too, even with options picked', () => {
    let state = toggleOption(createQuestionnaireState(), two, '첫째');
    state = setCustomText(state, two, '종\u0007소리');
    expect(isAnswered(state, two)).toBe(false);
    expect(isValid(two, pickFor(state, two))).toBe(false);
  });

  it('ignores text in a 직접 입력 row that is not chosen', () => {
    let state = setCustomText(createQuestionnaireState(), one, '종\u0007소리');
    state = toggleOption(state, one, '예');
    expect(pickFor(state, one).customChosen).toBe(false);
    expect(isAnswered(state, one)).toBe(true);
  });
});
