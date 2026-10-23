// MightyClaude.Core's UserQuestionnaire.Parse: the AskUserQuestion input an assistant entry (or a code block in one) may
// hold, read the way the host reads it, so the conversation shows the same questions the WinUI transcript does.
import { utf8Length } from './references';

export interface QuestionOption {
  label: string;
  description: string;
}

export interface Question {
  header: string;
  question: string;
  multiSelect: boolean;
  options: QuestionOption[];
}

/** Control characters (Unicode Cc) other than tab, line feed and carriage return. */
const forbiddenControl = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/;
/** A surrogate without its pair: text that is not valid UTF-16, which the host cannot read as JSON. */
const loneSurrogate = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;

function valid(value: unknown, maximum: number, empty = false): value is string {
  return typeof value === 'string' && utf8Length(value) <= maximum && (empty || value.trim().length > 0) && !forbiddenControl.test(value);
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The questions, or null when the text is not a complete, bounded questionnaire. */
export function parseQuestionnaire(input: string): Question[] | null {
  // A JSON object or nothing: nearly every reply is prose that should not cost a parse.
  if (!input.trimStart().startsWith('{') || utf8Length(input) > 65_536 || loneSurrogate.test(input)) return null;
  let root: unknown;
  try {
    root = JSON.parse(input);
  } catch {
    return null;
  }
  if (!isObject(root) || !Array.isArray(root.questions) || root.questions.length < 1 || root.questions.length > 4) return null;
  const result: Question[] = [];
  for (const item of root.questions as unknown[]) {
    if (!isObject(item) || !valid(item.header, 256) || !valid(item.question, 8192) || typeof item.multiSelect !== 'boolean') return null;
    const options = item.options;
    if (!Array.isArray(options) || options.length < 2 || options.length > 4) return null;
    const choices: QuestionOption[] = [];
    for (const option of options as unknown[]) {
      if (!isObject(option) || !valid(option.label, 1024) || !valid(option.description, 8192, true)) return null;
      choices.push({ label: option.label, description: option.description });
    }
    if (new Set(choices.map((choice) => choice.label)).size !== choices.length) return null;
    result.push({ header: item.header, question: item.question, multiSelect: item.multiSelect, options: choices });
  }
  return new Set(result.map((question) => question.question)).size === result.length ? result : null;
}
