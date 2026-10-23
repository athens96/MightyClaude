import { describe, expect, it } from 'vitest';
import { translator } from '../i18n/locale';
import { durationLabel, toolStateKey } from './format';
import { parseQuestionnaire } from './questionnaire';
import { localPath, textTargetAt, webUrl } from './references';

describe('textTargetAt', () => {
  const at = (text: string, word: string, offset = 1) => textTargetAt(text, text.indexOf(word) + offset);

  it('finds a relative path with its line', () => {
    expect(at('see src/app/App.tsx:42 for it', 'App')).toEqual({ path: 'src/app/App.tsx', line: 42 });
  });

  it('finds a bare file name and a Windows path', () => {
    expect(at('edit README.md now', 'README')).toEqual({ path: 'README.md', line: null });
    expect(at('at C:\\Work\\a.cs:7 x', 'Work')).toEqual({ path: 'C:\\Work\\a.cs', line: 7 });
  });

  it('keeps the spaces of a destination shown in parentheses', () => {
    expect(at('the notes (docs/my notes.md:3) here', 'notes.md')).toEqual({ path: 'docs/my notes.md', line: 3 });
  });

  it('never reads a URL suffix as a file', () => {
    expect(at('open https://example.com/a.html now', 'a.html')).toBeNull();
    expect(at('label (https://x.y/z.md)', 'z.md')).toBeNull();
  });

  it('looks only at the clicked line and needs a position inside the text', () => {
    expect(at('a.ts\nplain words', 'plain')).toBeNull();
    expect(textTargetAt('a.ts', 4)).toBeNull();
    expect(textTargetAt('a.ts', -1)).toBeNull();
  });

  it('ignores words without an extension', () => {
    expect(at('just words here', 'words')).toBeNull();
  });
});

describe('localPath / webUrl', () => {
  it('takes relative, drive and file: destinations', () => {
    expect(localPath('src/a.ts')).toBe('src/a.ts');
    expect(localPath('C:\\x\\y.txt')).toBe('C:\\x\\y.txt');
    expect(localPath('file:///C:/x/y%20z.txt')).toBe('C:/x/y z.txt');
    expect(localPath('file://host/share/a')).toBeNull();
    expect(localPath('https://a.b')).toBeNull();
    expect(localPath('javascript:alert(1)')).toBeNull();
    expect(localPath('')).toBeNull();
  });

  it('opens only http, https and mailto', () => {
    expect(webUrl('https://a.b/c')).toBe('https://a.b/c');
    expect(webUrl('mailto:a@b.c')).toBe('mailto:a@b.c');
    expect(webUrl('file:///c:/x')).toBeNull();
    expect(webUrl('javascript:alert(1)')).toBeNull();
    expect(webUrl('relative/path')).toBeNull();
  });
});

describe('parseQuestionnaire', () => {
  const option = (label: string, description = '') => ({ label, description });
  const question = (over: Record<string, unknown> = {}) => ({ header: 'Scope', question: 'Which?', multiSelect: false, options: [option('A'), option('B', 'second')], ...over });
  const parse = (value: unknown) => parseQuestionnaire(JSON.stringify(value));

  it('reads a valid questionnaire', () => {
    expect(parse({ questions: [question(), question({ question: 'Other?', multiSelect: true })] })).toEqual([
      { header: 'Scope', question: 'Which?', multiSelect: false, options: [option('A'), option('B', 'second')] },
      { header: 'Scope', question: 'Other?', multiSelect: true, options: [option('A'), option('B', 'second')] },
    ]);
  });

  it('answers prose and broken JSON with nothing', () => {
    expect(parseQuestionnaire('Sure, here is the plan.')).toBeNull();
    expect(parseQuestionnaire('{ "questions": [')).toBeNull();
    expect(parseQuestionnaire('[1]')).toBeNull();
  });

  it('holds the counts: 1-4 questions, 2-4 options', () => {
    expect(parse({ questions: [] })).toBeNull();
    expect(parse({ questions: Array.from({ length: 5 }, (_, i) => question({ question: 'q' + i })) })).toBeNull();
    expect(parse({ questions: [question({ options: [option('A')] })] })).toBeNull();
    expect(parse({ questions: [question({ options: ['A', 'B', 'C', 'D', 'E'].map((l) => option(l)) })] })).toBeNull();
  });

  it('needs every field with its type', () => {
    expect(parse({ questions: [question({ multiSelect: 'no' })] })).toBeNull();
    expect(parse({ questions: [question({ header: '  ' })] })).toBeNull();
    expect(parse({ questions: [question({ options: [{ label: 'A' }, option('B')] })] })).toBeNull();
  });

  it('refuses duplicate labels and duplicate questions', () => {
    expect(parse({ questions: [question({ options: [option('A'), option('A')] })] })).toBeNull();
    expect(parse({ questions: [question(), question()] })).toBeNull();
  });

  it('refuses control characters and text over its byte limit', () => {
    expect(parse({ questions: [question({ question: 'bell\u0007' })] })).toBeNull();
    expect(parse({ questions: [question({ question: 'tab\tand\nline' })] })).not.toBeNull();
    expect(parse({ questions: [question({ header: '가'.repeat(86) })] })).toBeNull();
    expect(parse({ questions: [question({ header: '가'.repeat(85) })] })).not.toBeNull();
    expect(parseQuestionnaire('{"questions":[],"pad":"' + 'x'.repeat(65_536) + '"}')).toBeNull();
  });
});

describe('durationLabel', () => {
  const t = translator([{ 'run.activity.durationSeconds': '{seconds}s', 'run.activity.durationMinutes': '{minutes}m', 'run.activity.durationMinutesSeconds': '{minutes}m {seconds}s' }]);
  const activity = (durationMs: number | undefined, state = 'completed', kind = 'command') => ({ id: 'a', provider: 'claude', kind, state, summary: '', durationMs });

  it('says nothing for a live, turn or unknown duration', () => {
    expect(durationLabel(activity(10, 'running'), t)).toBeNull();
    expect(durationLabel(activity(10, 'completed', 'turn'), t)).toBeNull();
    expect(durationLabel(activity(undefined), t)).toBeNull();
    expect(durationLabel(activity(-1), t)).toBeNull();
  });

  it('formats milliseconds, seconds and minutes', () => {
    expect(durationLabel(activity(0), t)).toBe('0ms');
    expect(durationLabel(activity(0.5), t)).toBe('<1ms');
    expect(durationLabel(activity(999.9), t)).toBe('999ms');
    expect(durationLabel(activity(1234), t)).toBe('1.2s');
    expect(durationLabel(activity(120_000), t)).toBe('2m');
    expect(durationLabel(activity(125_000, 'error'), t)).toBe('2m 5s');
  });

  it('names the state word', () => {
    expect(toolStateKey('waiting')).toBe('transcript.tool.waiting');
    expect(toolStateKey('error')).toBe('transcript.tool.failed');
    expect(toolStateKey('running')).toBe('transcript.tool.running');
  });
});
