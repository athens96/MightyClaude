import { describe, expect, it } from 'vitest';
import { inPath, markdown, maximumRenderBytes, parseInline, parseMarkdown, type Block, type Inline } from './markdown';

const text = (value: string): Inline => ({ type: 'text', text: value });
const paragraph = (...inline: Inline[]): Block => ({ type: 'paragraph', inline });

describe('blocks', () => {
  it('parts paragraphs on blank lines and joins soft breaks with a space', () => {
    expect(parseMarkdown('one\ntwo\n\nthree')).toEqual([paragraph(text('one two')), paragraph(text('three'))]);
  });

  it('keeps hard breaks: two trailing spaces or a trailing backslash', () => {
    expect(parseMarkdown('one  \ntwo\\\nthree')).toEqual([paragraph(text('one\ntwo\nthree'))]);
  });

  it('keeps the trailing backslash a Windows path ends in', () => {
    expect(parseMarkdown('see C:\\Work\\\nnext')).toEqual([paragraph(text('see C:\\Work\\ next'))]);
  });

  it('reads ATX headings of every level and drops closing hashes', () => {
    expect(parseMarkdown('# One\n## Two ##\n### Three\n#### Four\n####### seven')).toEqual([
      { type: 'heading', level: 1, inline: [text('One')] },
      { type: 'heading', level: 2, inline: [text('Two')] },
      { type: 'heading', level: 3, inline: [text('Three')] },
      { type: 'heading', level: 4, inline: [text('Four')] },
      paragraph(text('####### seven')),
    ]);
  });

  it('reads fenced code with its language, and an unclosed fence to the end', () => {
    expect(parseMarkdown('```ts\nconst a = 1;\n```\nafter')).toEqual([{ type: 'code', language: 'ts', text: 'const a = 1;' }, paragraph(text('after'))]);
    expect(parseMarkdown('~~~\nstill\nstreaming')).toEqual([{ type: 'code', language: null, text: 'still\nstreaming' }]);
  });

  it('does not read Markdown inside a fence', () => {
    expect(parseMarkdown('```\n# not a heading\n**x**\n```')).toEqual([{ type: 'code', language: null, text: '# not a heading\n**x**' }]);
  });

  it('reads thematic breaks', () => {
    expect(parseMarkdown('a\n\n---\n\n* * *\n___')).toEqual([paragraph(text('a')), { type: 'rule' }, { type: 'rule' }, { type: 'rule' }]);
  });

  it('nests quotes', () => {
    expect(parseMarkdown('> outer\n> > inner')).toEqual([
      { type: 'quote', children: [paragraph(text('outer')), { type: 'quote', children: [paragraph(text('inner'))] }] },
    ]);
  });

  it('reads unordered and ordered lists with their numbers and nested items', () => {
    expect(parseMarkdown('- a\n- b\n  - c')).toEqual([
      {
        type: 'list', ordered: false, items: [
          { ordinal: 1, task: null, children: [paragraph(text('a'))] },
          { ordinal: 2, task: null, children: [paragraph(text('b')), { type: 'list', ordered: false, items: [{ ordinal: 1, task: null, children: [paragraph(text('c'))] }] }] },
        ],
      },
    ]);
    const ordered = parseMarkdown('3. three\n4. four')[0];
    expect(ordered).toMatchObject({ type: 'list', ordered: true, items: [{ ordinal: 3 }, { ordinal: 4 }] });
  });

  it('reads task items', () => {
    expect(parseMarkdown('- [x] done\n- [ ] open\n- [X] also')[0]).toMatchObject({
      type: 'list', items: [{ task: 'checked', children: [paragraph(text('done'))] }, { task: 'open', children: [paragraph(text('open'))] }, { task: 'checked' }],
    });
  });

  it('reads GFM tables with their alignment, filling short rows', () => {
    expect(parseMarkdown('| a | b | c |\n|:--|:-:|--:|\n| 1 | **2** |')).toEqual([
      { type: 'table', align: ['left', 'center', 'right'], rows: [[[text('a')], [text('b')], [text('c')]], [[text('1')], [{ type: 'strong', children: [text('2')] }], []]] },
    ]);
  });

  it('ends a paragraph over a table head', () => {
    expect(parseMarkdown('intro\n| a |\n|---|\n| 1 |').map((block) => block.type)).toEqual(['paragraph', 'table']);
  });

  it('keeps an escaped pipe in its cell', () => {
    const [table] = parseMarkdown('| a \\| b |\n|---|');
    expect(table).toEqual({ type: 'table', align: ['left'], rows: [[[text('a | b')]]] });
  });

  it('shows a table wider than 63 columns as its lines', () => {
    const head = '|' + Array.from({ length: 64 }, (_, i) => 'c' + i).join('|') + '|';
    const rule = '|' + Array.from({ length: 64 }, () => '-').join('|') + '|';
    expect(parseMarkdown(head + '\n' + rule)).toEqual([{ type: 'source', lines: [head, rule] }]);
  });

  it('shows tables past 2,000 cells in all as their lines', () => {
    const rows = Array.from({ length: 1001 }, () => '| x | y |');
    const source = ['| a | b |', '|---|---|', ...rows].join('\n');
    expect(parseMarkdown(source)[0]!.type).toBe('source');
    const fits = ['| a | b |', '|---|---|', ...rows.slice(0, 999)].join('\n');
    expect(parseMarkdown(fits)[0]!.type).toBe('table');
  });

  it('shows a source over 128 KB as its lines', () => {
    const big = '# x\n' + 'a'.repeat(maximumRenderBytes);
    expect(parseMarkdown(big)).toEqual([{ type: 'source', lines: ['# x', 'a'.repeat(maximumRenderBytes)] }]);
  });

  it('stops nesting quotes past depth 8', () => {
    let blocks = parseMarkdown('>'.repeat(12) + ' deep');
    let depth = 0;
    while (blocks[0]?.type === 'quote') {
      blocks = blocks[0].children;
      depth++;
    }
    expect(depth).toBe(8);
    expect(blocks[0]).toEqual(paragraph(text('>>>> deep')));
  });

  it('shows the source as lines when it runs out of time', () => {
    expect(parseMarkdown('# a\nb', true, -1)).toEqual([{ type: 'source', lines: ['# a', 'b'] }]);
  });

  it('shows a questionnaire inside a code block', () => {
    const json = JSON.stringify({ questions: [{ header: 'H', question: 'Q?', multiSelect: false, options: [{ label: 'a', description: '' }, { label: 'b', description: 'd' }] }] });
    expect(parseMarkdown('```json\n' + json + '\n```')[0]).toMatchObject({ type: 'questionnaire', questions: [{ header: 'H' }] });
  });

  it('remembers a parse per text', () => {
    expect(markdown('**same**')).toBe(markdown('**same**'));
  });
});

describe('inline', () => {
  it('reads code spans, double-backtick spans trimmed', () => {
    expect(parseInline('a `x*y*` b ``  `q`  ``')).toEqual([text('a '), { type: 'code', text: 'x*y*' }, text(' b '), { type: 'code', text: '`q`' }]);
  });

  it('reads bold, italic, bold italic and strike, nested', () => {
    expect(parseInline('***bi*** **b _i_** __u__ *e* _f_ ~~s~~')).toEqual([
      { type: 'strongEm', children: [text('bi')] }, text(' '),
      { type: 'strong', children: [text('b '), { type: 'em', children: [text('i')] }] }, text(' '),
      { type: 'strong', children: [text('u')] }, text(' '),
      { type: 'em', children: [text('e')] }, text(' '),
      { type: 'em', children: [text('f')] }, text(' '),
      { type: 'strike', children: [text('s')] },
    ]);
  });

  it('leaves snake_case words alone', () => {
    expect(parseInline('a snake_case_name here')).toEqual([text('a snake_case_name here')]);
  });

  it('drops escapes in prose', () => {
    expect(parseInline('\\*literal\\* and 1\\. and snake\\_case')).toEqual([text('*literal* and 1. and snake_case')]);
  });

  it('keeps every backslash of a Windows path', () => {
    expect(parseInline('open C:\\Work\\.claude\\settings.json now')).toEqual([text('open C:\\Work\\.claude\\settings.json now')]);
    expect(parseInline('in native\\windows\\_build')).toEqual([text('in native\\windows\\_build')]);
  });

  it('tells web links, local links and other destinations apart', () => {
    expect(parseInline('[site](https://example.com) [mail](mailto:a@b.c) [file](src/a.ts:3) [x](javascript:alert(1))')).toEqual([
      { type: 'link', kind: 'web', target: 'https://example.com', children: [text('site')] }, text(' '),
      { type: 'link', kind: 'web', target: 'mailto:a@b.c', children: [text('mail')] }, text(' '),
      { type: 'link', kind: 'local', target: 'src/a.ts:3', children: [text('file')] }, text(' '),
      text('x'), text(')'),
    ]);
  });

  it('reads autolinks', () => {
    expect(parseInline('see <https://a.b/c> and <mailto:x@y.z>')).toEqual([text('see '), { type: 'autolink', url: 'https://a.b/c' }, text(' and '), { type: 'autolink', url: 'mailto:x@y.z' }]);
  });

  it('never fetches a remote picture and refuses an unknown scheme', () => {
    expect(parseInline('![shot](https://a.b/p.png)')).toEqual([{ type: 'image', kind: 'remote', alt: 'shot', source: 'https://a.b/p.png' }]);
    expect(parseInline('![x](ftp://a/b.png)')).toEqual([{ type: 'image', kind: 'refused', alt: 'x', source: 'ftp://a/b.png' }]);
    expect(parseInline('![l](shots/a.png)')).toEqual([{ type: 'image', kind: 'local', alt: 'l', source: 'shots/a.png' }]);
    expect(parseInline('![d](data:image/png;base64,AAAA)')).toEqual([{ type: 'image', kind: 'data', alt: 'd', source: 'data:image/png;base64,AAAA' }]);
  });

  it('shows a picture as its alt where pictures are not drawn', () => {
    expect(parseInline('![alt](a.png)', false)).toEqual([{ type: 'image', kind: 'alt', alt: 'alt', source: 'a.png' }]);
  });
});

describe('inPath', () => {
  it('reads drives, dot-names and shares as paths, prose and doubled backslashes not', () => {
    expect(inPath('C:\\_x', 2)).toBe(true);
    expect(inPath('"D:\\_y"', 3)).toBe(true);
    expect(inPath('MightyClaude\\.claude', 12)).toBe(true);
    expect(inPath('\\\\server\\share', 0)).toBe(true);
    expect(inPath('snake\\_case', 5)).toBe(false);
    expect(inPath('C:\\\\Users\\\\me', 2)).toBe(false);
    expect(inPath('a\\\\b', 1)).toBe(false);
  });
});
