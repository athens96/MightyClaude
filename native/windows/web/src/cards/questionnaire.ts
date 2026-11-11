// The question card's draft (MainWindow.Questionnaire.cs QuestionDraft and its buttons, M/UserQuestionnaireCard.swift): the step on
// screen, each step's picks, whether its own answer is chosen and its words. Claude asks one to four questions of two to four options;
// other agents' questions (ACP forms, Hermes's clarify, pi's dialogs) come to the same card with up to eight questions of up to twelve
// options, or none: such a question is answered in words only. Pure; the host checks the answers (UserQuestionnaire.ValidateAnswers)
// and sends them.
import type { Question } from './types';

export interface QuestionDraft {
  step: number;
  picks: Readonly<Record<number, readonly string[]>>;
  custom: Readonly<Record<number, boolean>>;
  text: Readonly<Record<number, string>>;
}

export const emptyDraft: QuestionDraft = { step: 0, picks: {}, custom: {}, text: {} };

/** The step's answer is typed: its own answer is chosen, or the question offers nothing to pick (the Mac's `typing`). */
export function typing(draft: QuestionDraft, questions: readonly Question[], step: number): boolean {
  return draft.custom[step] === true || questions[step]?.options.length === 0;
}

/** A step is answered by a pick, or by typed words (QuestionDraft.Answered). */
export function answered(draft: QuestionDraft, questions: readonly Question[], step: number): boolean {
  return (draft.picks[step]?.length ?? 0) > 0 || (typing(draft, questions, step) && (draft.text[step] ?? '').trim().length > 0);
}

/** Choosing an answer: a multiple question toggles it; a single one takes it alone and drops its own answer. */
export function pick(draft: QuestionDraft, question: Question, label: string): QuestionDraft {
  const step = draft.step;
  const current = draft.picks[step] ?? [];
  if (question.multiSelect) {
    const next = current.includes(label) ? current.filter((item) => item !== label) : [...current, label];
    return { ...draft, picks: { ...draft.picks, [step]: next } };
  }
  return { ...draft, picks: { ...draft.picks, [step]: [label] }, custom: { ...draft.custom, [step]: false } };
}

/** The own-answer tile: on, a single question drops its picks; off again, its words stay for later. */
export function toggleCustom(draft: QuestionDraft, question: Question): QuestionDraft {
  const step = draft.step;
  const on = draft.custom[step] !== true;
  const picks = on && !question.multiSelect ? { ...draft.picks, [step]: [] } : draft.picks;
  return { ...draft, picks, custom: { ...draft.custom, [step]: on } };
}

export function setText(draft: QuestionDraft, value: string): QuestionDraft {
  return { ...draft, text: { ...draft.text, [draft.step]: value } };
}

/** Next goes on only from an answered step. */
export function next(draft: QuestionDraft, questions: readonly Question[]): QuestionDraft {
  return draft.step + 1 < questions.length && answered(draft, questions, draft.step) ? { ...draft, step: draft.step + 1 } : draft;
}

export function back(draft: QuestionDraft): QuestionDraft {
  return draft.step > 0 ? { ...draft, step: draft.step - 1 } : draft;
}

/** A dot may be pressed for any step up to the one on screen, or a later one once every step before it is answered. */
export function canJump(draft: QuestionDraft, questions: readonly Question[], index: number): boolean {
  if (index <= draft.step) return true;
  for (let step = 0; step < index; step++) if (!answered(draft, questions, step)) return false;
  return true;
}

export function jump(draft: QuestionDraft, questions: readonly Question[], index: number): QuestionDraft {
  return canJump(draft, questions, index) ? { ...draft, step: index } : draft;
}

/** Send answers is on once every step is answered (the host checks the rest). */
export function canSubmit(draft: QuestionDraft, questions: readonly Question[]): boolean {
  return questions.every((_, step) => answered(draft, questions, step));
}

/** The answers in question order, each with the picks in the options' order and its words when typed. */
export function answers(draft: QuestionDraft, questions: readonly Question[]): { picks: string[]; custom: string | null }[] {
  return questions.map((question, step) => ({
    picks: question.options.map((option) => option.label).filter((label) => draft.picks[step]?.includes(label)),
    custom: typing(draft, questions, step) ? draft.text[step] ?? '' : null,
  }));
}
