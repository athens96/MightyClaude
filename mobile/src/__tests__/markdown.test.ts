import { isOpenableLink, prepareMarkdown } from '@/lib/markdown';

describe('prepareMarkdown', () => {
  it('leaves ordinary markdown untouched', () => {
    const text = '# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n[link](https://example.com)';
    expect(prepareMarkdown(text)).toBe(text);
  });

  it('closes a code fence the character budget cut open', () => {
    expect(prepareMarkdown('before\n```ts\nconst a = 1')).toBe('before\n```ts\nconst a = 1\n```');
    expect(prepareMarkdown('~~~~\ncode\n')).toBe('~~~~\ncode\n~~~~');
  });

  it('keeps a balanced fence as it is', () => {
    const text = '```\na\n```\ntext\n```py\nb\n```';
    expect(prepareMarkdown(text)).toBe(text);
  });

  it('turns task list items into ballot boxes', () => {
    expect(prepareMarkdown('- [ ] todo\n- [x] done\n1. [X] numbered')).toBe('- ☐ todo\n- ☑ done\n1. ☑ numbered');
  });

  it('normalises Windows line endings', () => {
    expect(prepareMarkdown('a\r\nb\rc')).toBe('a\nb\nc');
  });
});

describe('isOpenableLink', () => {
  it('opens web and mail links only', () => {
    expect(isOpenableLink('https://example.com')).toBe(true);
    expect(isOpenableLink('HTTP://x.y')).toBe(true);
    expect(isOpenableLink('mailto:a@b.c')).toBe(true);
    expect(isOpenableLink('file:///etc/passwd')).toBe(false);
    expect(isOpenableLink('javascript:alert(1)')).toBe(false);
    expect(isOpenableLink('./docs/relay.md')).toBe(false);
  });
});
