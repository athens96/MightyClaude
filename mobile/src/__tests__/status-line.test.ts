import {
  MAX_LINE_SEGMENTS,
  MAX_SEGMENT_TEXT,
  isHexColor,
  sanitiseRateLimits,
  sanitiseSegmentText,
  sanitiseStatusLines,
} from '@/lib/status-line';

describe('isHexColor', () => {
  it('accepts only #RRGGBB', () => {
    expect(isHexColor('#D97757')).toBe(true);
    expect(isHexColor('#d97757')).toBe(true);
    expect(isHexColor('#fff')).toBe(false);
    expect(isHexColor('#D97757AA')).toBe(false);
    expect(isHexColor('red')).toBe(false);
    expect(isHexColor('rgba(0,0,0,0.5)')).toBe(false);
    expect(isHexColor('')).toBe(false);
    expect(isHexColor(undefined)).toBe(false);
    expect(isHexColor(0xd97757)).toBe(false);
  });
});

describe('sanitiseStatusLines', () => {
  it('keeps text, valid colours and bold', () => {
    expect(
      sanitiseStatusLines({
        lines: [[{ text: 'main', fg: '#10A37F', bold: true }, { text: ' · 3 files' }]],
      }),
    ).toEqual([[{ text: 'main', fg: '#10A37F', bold: true }, { text: ' · 3 files' }]]);
  });

  it('drops a colour that is not #RRGGBB instead of rendering it', () => {
    expect(sanitiseStatusLines({ lines: [[{ text: 'x', fg: 'red' }]] })).toEqual([[{ text: 'x' }]]);
    expect(sanitiseStatusLines({ lines: [[{ text: 'x', fg: '#abc' }]] })).toEqual([[{ text: 'x' }]]);
    expect(sanitiseStatusLines({ lines: [[{ text: 'x', fg: 12345 }]] })).toEqual([[{ text: 'x' }]]);
  });

  it('keeps bold only when it is exactly true', () => {
    expect(sanitiseStatusLines({ lines: [[{ text: 'x', bold: 'yes' }]] })).toEqual([
      [{ text: 'x' }],
    ]);
  });

  it('drops segments without string text, and lines left empty', () => {
    expect(
      sanitiseStatusLines({
        lines: [[{ text: 'ok' }, { fg: '#000000' }, null, 3], [{ text: 7 }], 'nope'],
      }),
    ).toEqual([[{ text: 'ok' }]]);
  });

  it('drops a segment that control characters left empty, and the line with it', () => {
    expect(sanitiseStatusLines({ lines: [[{ text: '\u0007\u200E' }, { text: 'ok' }]] })).toEqual([
      [{ text: 'ok' }],
    ]);
    expect(sanitiseStatusLines({ lines: [[{ text: '\u202E' }]] })).toEqual([]);
  });

  it('stops at forty segments in one line', () => {
    const line = Array.from({ length: 60 }, (_, index) => ({ text: `s${index}` }));
    const sanitised = sanitiseStatusLines({ lines: [line] });
    expect(sanitised[0]).toHaveLength(MAX_LINE_SEGMENTS);
    expect(sanitised[0]?.[MAX_LINE_SEGMENTS - 1]?.text).toBe(`s${MAX_LINE_SEGMENTS - 1}`);
  });

  it('stops at six lines', () => {
    const lines = Array.from({ length: 9 }, (_, index) => [{ text: `line ${index}` }]);
    const sanitised = sanitiseStatusLines({ lines });
    expect(sanitised).toHaveLength(6);
    expect(sanitised[5]?.[0]?.text).toBe('line 5');
  });

  it('answers with nothing for a payload that is not a status line', () => {
    expect(sanitiseStatusLines(undefined)).toEqual([]);
    expect(sanitiseStatusLines(null)).toEqual([]);
    expect(sanitiseStatusLines({})).toEqual([]);
    expect(sanitiseStatusLines({ lines: 'main' })).toEqual([]);
  });
});

describe('sanitiseSegmentText', () => {
  it('strips C0 and C1 control characters, keeping the rest of the row', () => {
    expect(sanitiseSegmentText('ma\u0000in\u001B[31m·\u0007ok')).toBe('main[31m·ok');
    expect(sanitiseSegmentText('a\tb\nc\rd')).toBe('abcd');
    expect(sanitiseSegmentText('a\u007Fb\u0085c\u009Fd')).toBe('abcd');
  });

  it('strips the bidi controls that could reorder what the row seems to say', () => {
    expect(sanitiseSegmentText('\u202Emain\u202C')).toBe('main');
    expect(sanitiseSegmentText('a\u200Eb\u200Fc\u202Ad\u2066e\u2069f')).toBe('abcdef');
  });

  it('leaves ordinary text, Korean and emoji alone', () => {
    expect(sanitiseSegmentText('main · 3개 파일 ✅')).toBe('main · 3개 파일 ✅');
  });

  it('cuts the text at 256 characters', () => {
    expect(sanitiseSegmentText('x'.repeat(300))).toHaveLength(MAX_SEGMENT_TEXT);
    // The cut is applied after stripping, so control characters cannot eat the budget.
    expect(sanitiseSegmentText(`${'\u0000'.repeat(100)}${'y'.repeat(300)}`)).toHaveLength(
      MAX_SEGMENT_TEXT,
    );
  });
});

describe('sanitiseRateLimits', () => {
  it('keeps labelled bars and clamps the percentage', () => {
    expect(
      sanitiseRateLimits([
        { label: '5시간', usedPercent: 42.5, resetsAt: '2026-09-18T04:00:00.000Z' },
        { label: '주간', usedPercent: 140 },
        { label: '월간', usedPercent: -3 },
      ]),
    ).toEqual([
      { label: '5시간', usedPercent: 42.5, resetsAt: '2026-09-18T04:00:00.000Z' },
      { label: '주간', usedPercent: 100 },
      { label: '월간', usedPercent: 0 },
    ]);
  });

  it('drops entries without a label or a finite percentage', () => {
    expect(
      sanitiseRateLimits([
        { label: '', usedPercent: 10 },
        { usedPercent: 10 },
        { label: '주간', usedPercent: '10' },
        { label: '월간', usedPercent: Number.NaN },
        null,
        '주간',
      ]),
    ).toEqual([]);
  });

  it('keeps resetsAt only when it is a non-empty string', () => {
    expect(sanitiseRateLimits([{ label: 'a', usedPercent: 1, resetsAt: '' }])).toEqual([
      { label: 'a', usedPercent: 1 },
    ]);
    expect(sanitiseRateLimits([{ label: 'a', usedPercent: 1, resetsAt: 5 }])).toEqual([
      { label: 'a', usedPercent: 1 },
    ]);
  });

  it('answers with nothing for a payload that is not a list', () => {
    expect(sanitiseRateLimits(undefined)).toEqual([]);
    expect(sanitiseRateLimits({ label: 'a', usedPercent: 1 })).toEqual([]);
  });
});
