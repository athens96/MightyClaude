import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { formatModelId, modelLabel } from '@/lib/model-label';

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
