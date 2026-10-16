import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { antigravityModelLabel, formatModelId, modelLabel } from '@/lib/model-label';

/**
 * The same file `ModelLabelTests.swift` reads: the Mac and the phone must give every
 * model id the same label, so a mismatch here means the two disagree.
 */
const fixturePath = resolve(__dirname, '../../../native/contracts/fixtures/model-labels.json');
const cases = JSON.parse(readFileSync(fixturePath, 'utf8')) as {
  model: string;
  resolved?: string;
  fallback?: string;
  expected: string;
}[];

describe('model labels', () => {
  it('has a fixture covering every rule', () => {
    expect(cases.length).toBeGreaterThanOrEqual(40);
  });

  it.each(cases.map((entry) => [entry.model, entry.resolved, entry.fallback, entry.expected] as const))(
    'labels %j (resolved %j, fallback %j) as %j',
    (model, resolved, fallback, expected) => {
      expect(modelLabel(model, resolved, fallback)).toBe(expected);
    },
  );

  it('reads only full ids; aliases and default are not ids', () => {
    expect(formatModelId('claude-opus-5-5')).toBe('Opus 5.5');
    expect(formatModelId('opus')).toBeUndefined();
    expect(formatModelId('default')).toBeUndefined();
  });
});

/**
 * Antigravity slugs, tested per client rather than in the shared fixture
 * (`native/macos/Tests/MightyCoreTests/AntigravityTests.swift` holds the Mac's).
 */
describe('Antigravity slugs', () => {
  it('reads the effort a Gemini slug carries', () => {
    expect(formatModelId('gemini-4-argon-medium')).toBe('Gemini 4 Argon (Medium)');
    expect(formatModelId('gemini-3.8-flash-low')).toBe('Gemini 3.8 Flash (Low)');
    expect(formatModelId('gemini-2.5-flash-lite')).toBe('Gemini 2.5 Flash Lite');
    // A version and the effort word alone is not a slug with an effort.
    expect(formatModelId('gemini-3-high')).toBe('Gemini 3 High');
  });

  it("names agy's Claude and GPT-OSS slugs as agy models does", () => {
    expect(antigravityModelLabel('claude-opus-5-5-low')).toBe('Claude Opus 5.5 (Low)');
    expect(antigravityModelLabel('claude-sonnet-5-5-high')).toBe('Claude Sonnet 5.5 (High)');
    expect(antigravityModelLabel('gpt-oss-120b-medium')).toBe('GPT-OSS 120B (Medium)');
    expect(antigravityModelLabel('gemini-4-argon-high')).toBe('Gemini 4 Argon (High)');
    expect(antigravityModelLabel('default', undefined, 'Antigravity')).toBe('Antigravity');
    expect(antigravityModelLabel('claude-unknown-high')).toBe('claude-unknown-high');
  });
});
