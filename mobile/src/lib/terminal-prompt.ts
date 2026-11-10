import type { TerminalPrompt, TerminalPromptAnswerKind } from '@/api/types';

/** The most a typed answer may carry, as the host checks it (docs/terminal-prompts.md). */
export const MAXIMUM_ANSWER_BYTES = 4096;

const MARKED = /^[❯›>●◉◆→]/;

/** The answers each kind of prompt offers; anything else the host would reject as badAnswer. */
const ALLOWED: Record<TerminalPrompt['kind'], readonly TerminalPromptAnswerKind[]> = {
  yesNo: ['yes', 'no'],
  secret: ['text'],
  text: ['text', 'enter'],
  choice: ['up', 'down', 'enter', 'space'],
};

/** A choice line the terminal marks as the current one. */
export function isMarkedOption(line: string): boolean {
  return MARKED.test(line.trim());
}

/** Whether the prompt's default is "yes" (no default: yes is the primary answer). */
export function yesIsPrimary(prompt: Pick<TerminalPrompt, 'defaultYes'>): boolean {
  return prompt.defaultYes !== false;
}

/**
 * The body of `POST …/terminal-prompt` for one answer, or null when this kind does not take it
 * or a typed answer is empty or over 4,096 bytes. `text` goes with `text` only, as typed.
 */
export function terminalPromptBody(
  prompt: Pick<TerminalPrompt, 'id' | 'kind'>,
  answer: TerminalPromptAnswerKind,
  text?: string,
): { promptId: string; answer: TerminalPromptAnswerKind; text?: string } | null {
  if (!ALLOWED[prompt.kind].includes(answer)) return null;
  if (answer !== 'text') return { promptId: prompt.id, answer };
  const typed = text ?? '';
  const bytes = new TextEncoder().encode(typed).length;
  if (bytes < 1 || bytes > MAXIMUM_ANSWER_BYTES) return null;
  return { promptId: prompt.id, answer, text: typed };
}
