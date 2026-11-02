import { describe, expect, it } from 'vitest';
import { isHtmlPreview } from './HtmlView';
import type { FilePreview } from './types';

const preview = (name: string, extra: Partial<FilePreview> = {}): FilePreview => ({ path: 'docs/' + name, name, kind: 'source', banners: [], ...extra });

describe('isHtmlPreview', () => {
  it('takes a source preview of .html and .htm, in any case', () => {
    expect(isHtmlPreview(preview('index.html'))).toBe(true);
    expect(isHtmlPreview(preview('OLD.HTM'))).toBe(true);
  });
  it('leaves other files, failures and other kinds alone', () => {
    expect(isHtmlPreview(preview('app.ts'))).toBe(false);
    expect(isHtmlPreview(preview('page.html.txt'))).toBe(false);
    expect(isHtmlPreview(preview('index.html', { failure: 'x' }))).toBe(false);
    expect(isHtmlPreview(preview('index.html', { kind: 'unsupported' }))).toBe(false);
    expect(isHtmlPreview(null)).toBe(false);
  });
});
