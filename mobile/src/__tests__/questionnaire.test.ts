import type { Question } from '@/api/types';
import {
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
  setCustomText,
  toggleOption,
} from '@/lib/questionnaire';

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

  it('un-picks the single selected option when it is tapped again', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = toggleOption(state, one, '예');
    expect(pickFor(state, one).selectedOptions).toEqual([]);
    expect(isAnswered(state, one)).toBe(false);
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
  it('drops the free text as soon as an option is picked', () => {
    let state = setCustomText(createQuestionnaireState(), one, '조금 더 생각해 볼게요');
    state = toggleOption(state, one, '예');
    expect(pickFor(state, one)).toEqual({ selectedOptions: ['예'], customText: '' });
    expect(isAnswered(state, one)).toBe(true);
  });

  it('drops the picked option as soon as free text is typed', () => {
    let state = toggleOption(createQuestionnaireState(), one, '예');
    state = setCustomText(state, one, '다르게 해 주세요');
    expect(pickFor(state, one)).toEqual({ selectedOptions: [], customText: '다르게 해 주세요' });
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
    });
    expect(isAnswered(state, two)).toBe(true);
  });

  it('never lets 다음 or 답변 보내기 light up on a pick the host would reject', () => {
    // The two cannot be reached through the card any more, but a stale pick could still
    // hold both — the buttons stay dark rather than sending something rejected.
    const both = { picks: { [one.question]: { selectedOptions: ['예'], customText: '빠르게' } } };
    expect(isAnswered({ index: 0, ...both }, one)).toBe(false);
    expect(canGoNext(questions, { index: 0, ...both })).toBe(false);
    expect(isComplete([one], { index: 0, ...both })).toBe(false);
  });
});

describe('isValid', () => {
  it('mirrors the host: exactly one of an option and free text for a single pick', () => {
    expect(isValid(one, { selectedOptions: ['예'], customText: '' })).toBe(true);
    expect(isValid(one, { selectedOptions: [], customText: '따로 할게요' })).toBe(true);
    expect(isValid(one, { selectedOptions: ['예'], customText: '빠르게' })).toBe(false);
    expect(isValid(one, { selectedOptions: ['예', '아니오'], customText: '' })).toBe(false);
  });

  it('wants something chosen, whichever kind of question it is', () => {
    expect(isValid(one, { selectedOptions: [], customText: '  ' })).toBe(false);
    expect(isValid(two, { selectedOptions: [], customText: '' })).toBe(false);
  });

  it('lets a multi-select question carry several options and free text at once', () => {
    expect(isValid(two, { selectedOptions: ['첫째', '셋째'], customText: '그리고' })).toBe(true);
    expect(isValid(two, { selectedOptions: ['첫째', '셋째'], customText: '' })).toBe(true);
  });

  it('refuses a repeat and a label the question never offered', () => {
    expect(isValid(two, { selectedOptions: ['첫째', '첫째'], customText: '' })).toBe(false);
    expect(isValid(two, { selectedOptions: ['넷째'], customText: '' })).toBe(false);
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
