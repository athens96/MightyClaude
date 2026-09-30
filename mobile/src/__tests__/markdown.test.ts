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

describe('images in markdown', () => {
  // Imported lazily: the component pulls in the renderer, which the helpers above do not need.
  const { imageRule, rules, selectableRules } = require('@/components/assistant-markdown') as typeof import('@/components/assistant-markdown');
  const { Text } = require('react-native') as typeof import('react-native');
  const styles = { image_alt: { fontStyle: 'italic' } };
  const render = (attributes: Record<string, unknown>) =>
    imageRule({ key: 'img', type: 'image', sourceType: 'image', content: '', markup: '', children: [], attributes, index: 0 } as never, [], [], styles);

  it('shows the alt text and never an image source, for web addresses and bare paths alike', () => {
    for (const src of ['https://tracker.example/pixel.png', 'http://192.168.0.1/x.png', 'secret.png', '/Users/me/x.png', 'data:image/png;base64,AAAA']) {
      const element = render({ src, alt: 'diagram' }) as { type: unknown; props: Record<string, unknown> };
      expect(element.type).toBe(Text);
      expect(element.props.children).toBe('diagram');
      expect(JSON.stringify(element.props)).not.toContain(src);
    }
    expect(render({ src: 'https://tracker.example/pixel.png', alt: '  ' })).toBeNull();
    expect(render({ src: 'https://tracker.example/pixel.png' })).toBeNull();
  });

  it('is the rule for chat results and file previews alike', () => {
    expect(rules.image).toBe(imageRule);
    expect(selectableRules.image).toBe(imageRule);
  });
});
