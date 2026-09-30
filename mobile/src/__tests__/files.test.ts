import { ApiError, createClient, type RelayChannel } from '@/api/client';
import { RelayError } from '@/api/relay/transport';
import type { FileEntry, FilePreview } from '@/api/types';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';
import { previewNotices } from '@/components/source-view';
import {
  MAX_MARKDOWN_RENDER_BYTES,
  backTarget,
  baseName,
  breadcrumbs,
  describeFileError,
  displayName,
  filterEntries,
  formatModified,
  isFolder,
  isSymlink,
  parentPath,
  unsupportedReason,
  viewFor,
} from '@/lib/files';
import { resetLanguage, t } from '@/lib/i18n';

function fakeChannel(response: { status: number; body: unknown }) {
  const calls: { method: string; path: string; body?: unknown }[] = [];
  const channel: RelayChannel = {
    request: (method, path, body) => {
      calls.push({ method, path, body });
      return Promise.resolve(response);
    },
    onNotify: () => () => undefined,
  };
  return { channel, calls };
}

const listing = {
  protocol: 1,
  workspaceId: 'ws-1',
  path: 'src',
  truncated: false,
  entries: [
    { name: 'lib', relativePath: 'src/lib', kind: 'folder', noise: false },
    { name: 'App.swift', relativePath: 'src/App.swift', kind: 'file', size: 12, modified: '2026-09-30T01:02:03Z', noise: false },
  ],
};

function preview(fields: Partial<FilePreview>): FilePreview {
  return { protocol: 1, workspaceId: 'ws-1', path: 'a', name: 'a', size: 1, type: 'unsupported', ...fields };
}

beforeEach(() => resetLanguage('ko'));
afterEach(() => resetLanguage());

describe('file routes on the client', () => {
  it('lists the root without a query and a folder with its path percent-encoded', async () => {
    const fake = fakeChannel({ status: 200, body: listing });
    const client = createClient(fake.channel);
    await client.listFiles('ws-1', '');
    const result = await client.listFiles('ws 1', 'src/한글 폴더/a&b=c+d#e?');
    expect(fake.calls.map((call) => `${call.method} ${call.path}`)).toEqual([
      'GET /m1/workspaces/ws-1/files',
      'GET /m1/workspaces/ws%201/files?path=src%2F%ED%95%9C%EA%B8%80%20%ED%8F%B4%EB%8D%94%2Fa%26b%3Dc%2Bd%23e%3F',
    ]);
    expect(fake.calls.every((call) => call.body === undefined)).toBe(true);
    expect(result.entries.map((entry) => entry.kind)).toEqual(['folder', 'file']);
  });

  it('asks for one file by its path', async () => {
    const fake = fakeChannel({ status: 200, body: preview({ type: 'source', text: 'x', language: 'swift' }) });
    const result = await createClient(fake.channel).filePreview('ws-1', 'Sources/App.swift');
    expect(fake.calls[0]).toEqual({ method: 'GET', path: '/m1/workspaces/ws-1/file?path=Sources%2FApp.swift', body: undefined });
    expect(result.language).toBe('swift');
  });

  it('keeps the host’s error code beside its message', async () => {
    const fake = fakeChannel({ status: 403, body: { protocol: 1, error: '워크스페이스 밖의 경로입니다.', code: 'outsideWorkspace' } });
    const failure = await createClient(fake.channel).filePreview('ws-1', 'link').catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 403, code: 'outsideWorkspace', message: '워크스페이스 밖의 경로입니다.' });
    expect(describeFileError(failure)).toBe('워크스페이스 밖을 가리키는 경로라 열 수 없습니다.');

    const plain = fakeChannel({ status: 404, body: { protocol: 1, error: '모바일 경로를 찾을 수 없습니다.' } });
    const older = await createClient(plain.channel).listFiles('ws-1', '').catch((error: unknown) => error);
    expect((older as ApiError).code).toBeUndefined();
    expect(describeFileError(older)).toBe('모바일 경로를 찾을 수 없습니다.');
    expect(describeFileError(new RelayError('timeout'))).toBe('응답 시간이 초과되었습니다');
  });

  it('names every refusal the host can send', () => {
    for (const code of ['workspaceNotFound', 'notFound', 'outsideWorkspace', 'notReadable', 'notDirectory', 'badPath']) {
      const text = describeFileError(new ApiError(400, 'host words', code));
      expect(text).not.toBe('host words');
      expect(text).not.toContain('phone.files');
    }
    expect(describeFileError(new ApiError(400, 'host words', 'somethingNew'))).toBe('host words');
  });

  it('says a path too long for the host’s tunnel is too long, Hangul percent-encoded included', async () => {
    // 1,365 syllables are 4,095 UTF-8 bytes, the route's own limit; nine bytes each once encoded.
    const long = `docs/${'가'.repeat(1_363)}`;
    const fake = fakeChannel({ status: 414, body: { protocol: 1, error: '경로가 너무 깁니다.', code: 'badPath' } });
    const failure = await createClient(fake.channel).filePreview('ws-1', long).catch((error: unknown) => error);
    const sent = fake.calls[0]?.path.length ?? 0;
    expect(sent).toBeGreaterThan(12_000);
    expect(sent).toBeLessThanOrEqual(16 * 1024);
    expect(failure).toMatchObject({ status: 414, code: 'badPath' });
    expect(describeFileError(failure)).toBe('경로가 너무 길어 Mac에 보낼 수 없습니다.');
    expect(describeFileError(new ApiError(400, 'x', 'badPath'))).toBe('경로가 올바르지 않습니다.');
  });

  it('is offered only by a host that advertises it', () => {
    expect(hasCapability(parseCapabilities({ capabilities: ['style', 'files'] }), 'files')).toBe(true);
    expect(hasCapability(parseCapabilities({ capabilities: ['style'] }), 'files')).toBe(false);
    expect(hasCapability(parseCapabilities({}), 'files')).toBe(false);
  });
});

describe('paths and breadcrumbs', () => {
  it('walks up and names the last component', () => {
    expect(parentPath('a/b/c.txt')).toBe('a/b');
    expect(parentPath('a')).toBe('');
    expect(parentPath('')).toBe('');
    expect(baseName('a/b/c.txt')).toBe('c.txt');
    expect(baseName('top')).toBe('top');
  });

  it('labels the root with the workspace and every folder down to the current one', () => {
    expect(breadcrumbs('', 'Repo')).toEqual([{ label: 'Repo', path: '' }]);
    expect(breadcrumbs('src/lib/deep', 'Repo')).toEqual([
      { label: 'Repo', path: '' },
      { label: 'src', path: 'src' },
      { label: 'lib', path: 'src/lib' },
      { label: 'deep', path: 'src/lib/deep' },
    ]);
  });

  it('shows a decomposed Korean name composed but keeps the path as the host sent it', () => {
    const decomposed = '한글'.normalize('NFD');
    expect(decomposed).not.toBe('한글');
    expect(displayName(decomposed)).toBe('한글');
    expect(breadcrumbs(`docs/${decomposed}`, 'Repo')[2]).toEqual({ label: '한글', path: `docs/${decomposed}` });
  });

  it('walks back up one folder at a time, then lets back leave the screen', () => {
    expect(backTarget('a/b', { type: 'GO_BACK' })).toBe('a');
    expect(backTarget('a', { type: 'POP', payload: { count: 1 } })).toBe('');
    expect(backTarget('a/b', { type: 'POP' })).toBe('a');
    expect(backTarget('', { type: 'GO_BACK' })).toBeUndefined();
    expect(backTarget('a/b', { type: 'POP', payload: { count: 2 } })).toBeUndefined();
    expect(backTarget('a/b', { type: 'POP_TO_TOP' })).toBeUndefined();
    expect(backTarget('a/b', { type: 'RESET' })).toBeUndefined();
  });

  it('tells folders and links apart by kind', () => {
    expect(['folder', 'symlink-folder', 'file', 'symlink-file'].map(isFolder)).toEqual([true, true, false, false]);
    expect(['folder', 'symlink-folder', 'file', 'symlink-file'].map(isSymlink)).toEqual([false, true, false, true]);
  });
});

describe('the name filter', () => {
  const entries: FileEntry[] = [
    { name: 'README.md', relativePath: 'README.md', kind: 'file', noise: false },
    { name: 'readme-old.txt', relativePath: 'readme-old.txt', kind: 'file', noise: false },
    { name: '보고서.md'.normalize('NFD'), relativePath: '보고서.md', kind: 'file', noise: false },
    { name: 'src', relativePath: 'src', kind: 'folder', noise: false },
  ];

  it('ignores case, surrounding space and composition, and keeps the host order', () => {
    expect(filterEntries(entries, '  ReadMe ').map((entry) => entry.name)).toEqual(['README.md', 'readme-old.txt']);
    expect(filterEntries(entries, '보고').map((entry) => entry.relativePath)).toEqual(['보고서.md']);
    expect(filterEntries(entries, '').length).toBe(4);
    expect(filterEntries(entries, '   ')).not.toBe(entries);
    expect(filterEntries(entries, 'zzz')).toEqual([]);
  });
});

describe('choosing the viewer', () => {
  it('shows source and markdown only with their text', () => {
    expect(viewFor(preview({ type: 'source', text: 'let x', language: 'swift' }))).toEqual({ kind: 'source', text: 'let x', language: 'swift' });
    expect(viewFor(preview({ type: 'source', text: '' }))).toEqual({ kind: 'source', text: '', language: 'plain' });
    expect(viewFor(preview({ type: 'source' }))).toEqual({ kind: 'unsupported' });
    expect(viewFor(preview({ type: 'markdown', text: '# Hi' }))).toEqual({ kind: 'markdown', text: '# Hi', renderable: true });
    expect(viewFor(preview({ type: 'markdown' }))).toEqual({ kind: 'unsupported' });
  });

  it('renders markdown up to the Mac’s limit, counted in UTF-8 bytes', () => {
    const atLimit = 'a'.repeat(MAX_MARKDOWN_RENDER_BYTES);
    expect(viewFor(preview({ type: 'markdown', text: atLimit }))).toMatchObject({ renderable: true });
    expect(viewFor(preview({ type: 'markdown', text: `${atLimit}a` }))).toMatchObject({ renderable: false });
    // 43,691 Hangul syllables are 131,073 bytes: over, though far fewer characters.
    expect(viewFor(preview({ type: 'markdown', text: '가'.repeat(43_691) }))).toMatchObject({ renderable: false });
  });

  it('draws an image only from a known format with data', () => {
    expect(viewFor(preview({ type: 'image', mime: 'image/png', data: 'AAAA' }))).toEqual({ kind: 'image', uri: 'data:image/png;base64,AAAA' });
    expect(viewFor(preview({ type: 'image', mime: 'image/jpeg', data: 'AAAA' }))).toMatchObject({ kind: 'image' });
    expect(viewFor(preview({ type: 'image', mime: 'image/png' }))).toEqual({ kind: 'unsupported' });
    expect(viewFor(preview({ type: 'image', mime: 'image/gif' as 'image/png', data: 'AAAA' }))).toEqual({ kind: 'unsupported' });
  });

  it('treats a type it does not know as unsupported', () => {
    expect(viewFor(preview({ type: 'video' as 'source', text: 'x' }))).toEqual({ kind: 'unsupported' });
    expect(viewFor(preview({ type: 'unsupported', reason: 'binary' }))).toEqual({ kind: 'unsupported' });
  });

  it('says why a file has no preview', () => {
    const reasons = (['binary', 'notRegularFile', 'tooLarge', 'undecodable'] as const).map((reason) =>
      unsupportedReason(preview({ reason })),
    );
    expect(new Set(reasons).size).toBe(4);
    expect(unsupportedReason(preview({}))).toBe(reasons[0]);
  });

  it('always says a file was cut short, markdown too large to render included', () => {
    const cut = t('phone.files.source.truncated');
    const big = { kind: 'markdown', text: '# big', renderable: false } as const;
    expect(previewNotices(big, true, false)).toEqual([t('phone.files.markdown.tooLarge'), cut]);
    expect(previewNotices(big, false, false)).toEqual([t('phone.files.markdown.tooLarge')]);
    const small = { kind: 'markdown', text: '# small', renderable: true } as const;
    expect(previewNotices(small, true, false)).toEqual([cut]);
    expect(previewNotices(small, true, true)).toEqual([cut]);
    expect(previewNotices({ kind: 'source', text: 'x', language: 'plain' }, true, false)).toEqual([cut]);
    expect(previewNotices({ kind: 'image', uri: 'data:' }, false, false)).toEqual([t('phone.files.image.hint')]);
    expect(previewNotices(undefined, true, false)).toEqual([]);
  });

  it('formats a date and leaves out one it cannot read', () => {
    expect(formatModified('2026-09-30T01:02:03Z', 'en-US')).toContain('2026');
    expect(formatModified(undefined)).toBe('');
    expect(formatModified('not a date')).toBe('');
  });
});
