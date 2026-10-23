// The question card's draft (MainWindow.Questionnaire.cs QuestionDraft and its buttons): the step on screen, each step's
// picks, whether its own answer is chosen and its words. Pure; the host checks the answers (UserQuestionnaire.ValidateAnswers)
// and sends them.
import type { Question } from './types';

export interface QuestionDraft {
  step: number;
  picks: Readonly<Record<number, readonly string[]>>;
  custom: Readonly<Record<number, boolean>>;
  text: Readonly<Record<number, string>>;
}

export const emptyDraft: QuestionDraft = { step: 0, picks: {}, custom: {}, text: {} };

/** A step is answered by a pick, or by its own answer with words in it (QuestionDraft.Answered). */
export function answered(draft: QuestionDraft, step: number): boolean {
  return (draft.picks[step]?.length ?? 0) > 0 || (draft.custom[step] === true && (draft.text[step] ?? '').trim().length > 0);
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
export function next(draft: QuestionDraft, total: number): QuestionDraft {
  return draft.step + 1 < total && answered(draft, draft.step) ? { ...draft, step: draft.step + 1 } : draft;
}

export function back(draft: QuestionDraft): QuestionDraft {
  return draft.step > 0 ? { ...draft, step: draft.step - 1 } : draft;
}

/** A dot may be pressed for any step up to the one on screen, or a later one once every step before it is answered. */
export function canJump(draft: QuestionDraft, index: number): boolean {
  if (index <= draft.step) return true;
  for (let step = 0; step < index; step++) if (!answered(draft, step)) return false;
  return true;
}

export function jump(draft: QuestionDraft, index: number): QuestionDraft {
  return canJump(draft, index) ? { ...draft, step: index } : draft;
}

/** Send answers is on once every step is answered (the host checks the rest). */
export function canSubmit(draft: QuestionDraft, total: number): boolean {
  for (let step = 0; step < total; step++) if (!answered(draft, step)) return false;
  return true;
}

/** The answers in question order, each with the picks in the options' order and its own words when chosen. */
export function answers(draft: QuestionDraft, questions: readonly Question[]): { picks: string[]; custom: string | null }[] {
  return questions.map((question, step) => ({
    picks: question.options.map((option) => option.label).filter((label) => draft.picks[step]?.includes(label)),
    custom: draft.custom[step] === true ? draft.text[step] ?? '' : null,
  }));
}
