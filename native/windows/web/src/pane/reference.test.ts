import { describe, expect, it } from 'vitest';
import { bubbleDrag, bubbleFrame, lineScrollTop, referenceName, referenceSymbol, savedBubbleSize, tailTop, wantedBubbleSize } from './reference';

describe('reference bubble (ReferencePreview.BubbleSize, FitReferencePreview)', () => {
  it('a saved size that is not a positive number is none; neither goes below its least', () => {
    expect(savedBubbleSize(undefined, null)).toEqual({ width: null, height: null });
    expect(savedBubbleSize(Number.NaN, -4)).toEqual({ width: null, height: null });
    expect(savedBubbleSize(120, 80)).toEqual({ width: 300, height: 200 });
    expect(savedBubbleSize(640, 480)).toEqual({ width: 640, height: 480 });
  });
  it('opens 420 wide and as tall as the room until a size is saved', () => {
    expect(wantedBubbleSize(undefined, undefined)).toEqual({ width: 420, height: 0 });
    expect(wantedBubbleSize(500, 300)).toEqual({ width: 500, height: 300 });
  });
  it('fits the room: at most 85% of its width, its height, 12 from each edge, never under 300×200', () => {
    expect(bubbleFrame(1000, 800, { width: 420, height: 0 })).toEqual({ width: 420, height: 776 });
    expect(bubbleFrame(1000, 800, { width: 2000, height: 2000 })).toEqual({ width: (1000 - 24) * 0.85, height: 776 });
    expect(bubbleFrame(200, 100, { width: 420, height: 0 })).toEqual({ width: 300, height: 200 });
    expect(bubbleFrame(1000, 800, { width: 350, height: 260 })).toEqual({ width: 350, height: 260 });
  });
  it('the grip widens toward the content: leftward on the right, rightward on the left', () => {
    expect(bubbleDrag({ width: 420, height: 500 }, -30, 20, false)).toEqual({ width: 450, height: 520 });
    expect(bubbleDrag({ width: 420, height: 500 }, 30, -400, true)).toEqual({ width: 450, height: 200 });
  });
  it('the tail stands at the head, or half-way down a short bubble', () => {
    expect(tailTop(600, 30)).toBe(21);
    expect(tailTop(40, 30)).toBe(11);
  });
  it('names, symbols and the line scroll', () => {
    expect(referenceName('src/a.ts')).toBe('a.ts');
    expect(referenceName('C:\\w\\b.md')).toBe('b.md');
    expect(referenceName('dir/')).toBe('dir');
    expect(referenceSymbol('README.MD')).toBe('markdown');
    expect(referenceSymbol('x/y.png')).toBe('image');
    expect(referenceSymbol('index.htm')).toBe('html');
    expect(referenceSymbol('a.d/file')).toBe('source');
    expect(lineScrollTop(1, 17)).toBe(0);
    expect(lineScrollTop(10, 17)).toBe(153);
    expect(lineScrollTop(null, 17)).toBe(0);
  });
});
