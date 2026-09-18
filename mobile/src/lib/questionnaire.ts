import type { Question, QuestionAnswer, QuestionAnswers } from '@/api/types';

/**
 * State machine behind the one-question-at-a-time questionnaire card.
 *
 * Picks live only here (and therefore only in the card's component state): if the
 * connection drops mid-flow nothing is persisted — the picks survive as long as the card
 * is mounted and are discarded the moment the request disappears from the session detail.
 */

export interface QuestionPick {
  selectedOptions: string[];
  customText: string;
}

export interface QuestionnaireState {
  /** Index of the question on screen. */
  index: number;
  /** Keyed by question text, the same key the host expects in the answer map. */
  picks: Record<string, QuestionPick>;
}

const EMPTY_PICK: QuestionPick = { selectedOptions: [], customText: '' };

export function createQuestionnaireState(): QuestionnaireState {
  return { index: 0, picks: {} };
}

/** Clamps a stored index against the current question list. */
function clampIndex(questions: readonly Question[], index: number): number {
  if (questions.length === 0) return 0;
  return Math.min(questions.length - 1, Math.max(0, index));
}

export function currentQuestion(
  questions: readonly Question[],
  state: QuestionnaireState,
): Question | undefined {
  return questions[clampIndex(questions, state.index)];
}

export function pickFor(state: QuestionnaireState, question: Question): QuestionPick {
  return state.picks[question.question] ?? EMPTY_PICK;
}

function withPick(
  state: QuestionnaireState,
  question: Question,
  next: QuestionPick,
): QuestionnaireState {
  return { ...state, picks: { ...state.picks, [question.question]: next } };
}

/**
 * Single-select replaces the choice and drops the free text with it; multi-select adds
 * or removes the option and leaves the free text alone. The host accepts an option *or*
 * free text for a single-select question, never both, so the card never lets the user
 * build an answer that would come back rejected.
 */
export function toggleOption(
  state: QuestionnaireState,
  question: Question,
  label: string,
): QuestionnaireState {
  const pick = pickFor(state, question);
  const selected = pick.selectedOptions.includes(label);
  if (question.multiSelect) {
    const selectedOptions = selected
      ? pick.selectedOptions.filter((entry) => entry !== label)
      : [...pick.selectedOptions, label];
    return withPick(state, question, { ...pick, selectedOptions });
  }
  if (selected) return withPick(state, question, { ...pick, selectedOptions: [] });
  return withPick(state, question, { selectedOptions: [label], customText: '' });
}

/** The other half of the single-select rule: real free text clears the single pick. */
export function setCustomText(
  state: QuestionnaireState,
  question: Question,
  customText: string,
): QuestionnaireState {
  const pick = pickFor(state, question);
  const clears = !question.multiSelect && customText.trim().length > 0;
  return withPick(state, question, {
    selectedOptions: clears ? [] : pick.selectedOptions,
    customText,
  });
}

/**
 * The phone's copy of the host's own check (`UserQuestionnaire.validatedAnswers`):
 * known labels, no repeats, something chosen, and for a single-select question exactly
 * one of "an option" and "free text".
 */
export function isValid(question: Question, pick: QuestionPick): boolean {
  const selections = new Set(pick.selectedOptions);
  if (selections.size !== pick.selectedOptions.length) return false;
  const labels = new Set(question.options.map((option) => option.label));
  for (const label of selections) {
    if (!labels.has(label)) return false;
  }
  const custom = pick.customText.trim();
  if (selections.size === 0 && custom.length === 0) return false;
  if (question.multiSelect) return true;
  return selections.size + (custom.length === 0 ? 0 : 1) === 1;
}

/** Answered means the pick would survive the host's check. */
export function isAnswered(state: QuestionnaireState, question: Question): boolean {
  return isValid(question, pickFor(state, question));
}

export function isLastQuestion(
  questions: readonly Question[],
  state: QuestionnaireState,
): boolean {
  return clampIndex(questions, state.index) >= questions.length - 1;
}

export function canGoBack(state: QuestionnaireState): boolean {
  return state.index > 0;
}

/** "다음" is live only once the question on screen has an answer. */
export function canGoNext(questions: readonly Question[], state: QuestionnaireState): boolean {
  const question = currentQuestion(questions, state);
  if (!question || isLastQuestion(questions, state)) return false;
  return isAnswered(state, question);
}

export function goNext(
  questions: readonly Question[],
  state: QuestionnaireState,
): QuestionnaireState {
  if (!canGoNext(questions, state)) return state;
  return { ...state, index: clampIndex(questions, state.index) + 1 };
}

export function goBack(state: QuestionnaireState): QuestionnaireState {
  if (!canGoBack(state)) return state;
  return { ...state, index: state.index - 1 };
}

/**
 * Which progress dots can be tapped: going back is always allowed, going forward only
 * across questions that already have an answer — the same rule "다음" follows.
 */
export function canGoTo(
  questions: readonly Question[],
  state: QuestionnaireState,
  index: number,
): boolean {
  if (questions.length === 0) return false;
  const target = clampIndex(questions, index);
  const current = clampIndex(questions, state.index);
  for (let step = current; step < target; step += 1) {
    const question = questions[step];
    if (!question || !isAnswered(state, question)) return false;
  }
  return true;
}

/** Jumps straight to a question, used when the user taps the progress dots. */
export function goTo(
  questions: readonly Question[],
  state: QuestionnaireState,
  index: number,
): QuestionnaireState {
  return { ...state, index: clampIndex(questions, index) };
}

/** Every question answered; the send button waits for this. */
export function isComplete(questions: readonly Question[], state: QuestionnaireState): boolean {
  if (questions.length === 0) return false;
  return questions.every((question) => isAnswered(state, question));
}

/** "질문 2/3" — only shown when there is more than one question. */
export function progressLabel(questions: readonly Question[], state: QuestionnaireState): string {
  return `질문 ${clampIndex(questions, state.index) + 1}/${questions.length}`;
}

/** The wire shape: `{ "<question text>": { selectedOptions, customText? } }`. */
export function buildAnswers(
  questions: readonly Question[],
  state: QuestionnaireState,
): QuestionAnswers {
  const answers: QuestionAnswers = {};
  for (const question of questions) {
    const pick = pickFor(state, question);
    const customText = pick.customText.trim();
    const answer: QuestionAnswer = { selectedOptions: [...pick.selectedOptions] };
    if (customText.length > 0) answer.customText = customText;
    answers[question.question] = answer;
  }
  return answers;
}
