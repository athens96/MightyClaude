import { describe, expect, it } from 'vitest';
import { chipClass, edgeClass, tintClass } from './structureTone';

describe('pipeline state colours', () => {
  it('turns the host tone into a class, none for a step to come or an unknown word', () => {
    expect(['run', 'wait', 'done', 'err', 'stop'].map((tone) => tintClass(tone))).toEqual([
      ' tinted tint-run', ' tinted tint-wait', ' tinted tint-done', ' tinted tint-err', ' tinted tint-stop',
    ]);
    expect(tintClass(null)).toBe('');
    expect(tintClass(undefined)).toBe('');
    expect(tintClass('idle')).toBe('');
    expect(tintClass('constructor')).toBe('');
  });

  it('colours only the current step\'s chip, in its column\'s tone', () => {
    expect(['run', 'wait', 'done', 'err', 'stop'].map((tone) => chipClass('current', tone))).toEqual([' chip-run', ' chip-wait', ' chip-done', ' chip-err', ' chip-stop']);
    expect(chipClass('current', null)).toBe('');
    expect(chipClass('current', 'idle')).toBe('');
    expect(chipClass('done', 'done')).toBe('');
    expect(chipClass('pending', null)).toBe('');
  });

  it('gives a waiting card the wait edge, not the running blue', () => {
    expect(edgeClass('running')).toBe(' live');
    expect(edgeClass('waiting')).toBe(' waiting');
    expect(edgeClass('completed')).toBe('');
    expect(edgeClass('error')).toBe('');
    expect(edgeClass('stopped')).toBe('');
  });
});
