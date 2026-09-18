import { providerMarkOutline, providerMarkOutlines } from '@/lib/provider-marks';

type Command = { kind: string; values: number[] };

/** `M1 2 C…Z`: a letter, then its numbers separated by spaces — as the Mac app reads it. */
function commands(outline: string): Command[] {
  return outline
    .split(/(?=[MLCZ])/)
    .map((part) => part.trim())
    .filter((part) => part.length > 0)
    .map((part) => ({
      kind: part.slice(0, 1),
      values: part
        .slice(1)
        .split(' ')
        .filter((value) => value.length > 0)
        .map(Number),
    }));
}

const valuesPerCommand: Record<string, number> = { M: 2, L: 2, C: 6, Z: 0 };

/**
 * The points the outline actually passes through: every `M`/`L` target and the end point
 * of every curve. A curve's two control points are allowed to sit a little outside the
 * box — that is how a circle is approximated — so they are not on-curve points.
 */
function onCurvePoints(outline: string): number[] {
  const points: number[] = [];
  for (const { kind, values } of commands(outline)) {
    if (kind === 'M' || kind === 'L') points.push(...values);
    if (kind === 'C') points.push(...values.slice(4));
  }
  return points;
}

describe('provider marks', () => {
  const names = ['claude', 'codex', 'gemini'] as const;

  it.each(names)('%s is a closed outline of absolute commands', (name) => {
    const outline = providerMarkOutlines[name];
    expect(outline.length).toBeGreaterThan(0);
    expect(outline.startsWith('M')).toBe(true);
    expect(outline.endsWith('Z')).toBe(true);
    expect(outline).toMatch(/^[MLCZ0-9 .-]+$/);
  });

  it.each(names)('%s only uses commands the Mac app can draw', (name) => {
    const parsed = commands(providerMarkOutlines[name]);
    expect(parsed.length).toBeGreaterThan(0);
    expect(parsed[0]?.kind).toBe('M');
    for (const { kind, values } of parsed) {
      expect(valuesPerCommand[kind]).toBeDefined();
      expect(values).toHaveLength(valuesPerCommand[kind] ?? -1);
      for (const value of values) expect(Number.isFinite(value)).toBe(true);
    }
  });

  it.each(names)('%s stays inside the 24 box', (name) => {
    const points = onCurvePoints(providerMarkOutlines[name]);
    expect(points.length).toBeGreaterThan(0);
    for (const value of points) {
      expect(value).toBeGreaterThanOrEqual(-0.01);
      expect(value).toBeLessThanOrEqual(24.01);
    }
  });

  it('gives nothing back for a provider we do not know', () => {
    expect(providerMarkOutline('claude')).toBe(providerMarkOutlines.claude);
    expect(providerMarkOutline('rovo')).toBeUndefined();
    expect(providerMarkOutline('')).toBeUndefined();
  });
});
