import { makeSliceReader, type SliceHandle } from '@/lib/file-slices';

/** A file on "disk", opened through a handle that records how it was driven. */
function fakeFiles(contents: Record<string, Uint8Array>) {
  const opened: string[] = [];
  let live = 0;
  const open = (uri: string): SliceHandle => {
    const bytes = contents[uri];
    if (!bytes) throw new Error(`파일이 없습니다: ${uri}`);
    opened.push(uri);
    live += 1;
    let closed = false;
    return {
      offset: 0,
      readBytes(length: number) {
        if (closed) throw new Error('닫힌 핸들입니다.');
        const start = this.offset ?? 0;
        // Short at the end of the file, exactly as a real handle answers.
        return bytes.slice(start, start + length);
      },
      close() {
        if (closed) return;
        closed = true;
        live -= 1;
      },
    };
  };
  return {
    open,
    opened,
    get live() {
      return live;
    },
  };
}

const body = Uint8Array.from({ length: 250 }, (_, index) => index % 256);

describe('makeSliceReader', () => {
  it('reads exactly the range it was asked for', async () => {
    const files = fakeFiles({ 'file:///a.bin': body });
    const read = makeSliceReader(files.open);

    await expect(read('file:///a.bin', 0, 100)).resolves.toEqual(body.slice(0, 100));
    await expect(read('file:///a.bin', 100, 100)).resolves.toEqual(body.slice(100, 200));
    await expect(read('file:///a.bin', 200, 50)).resolves.toEqual(body.slice(200, 250));
  });

  it('closes the handle after every chunk, so a 5 MB file holds one at a time', async () => {
    const files = fakeFiles({ 'file:///a.bin': body });
    const read = makeSliceReader(files.open);

    await read('file:///a.bin', 0, 100);
    await read('file:///a.bin', 100, 100);
    expect(files.opened).toEqual(['file:///a.bin', 'file:///a.bin']);
    expect(files.live).toBe(0);
  });

  it('answers short at the end of the file instead of padding', async () => {
    const files = fakeFiles({ 'file:///a.bin': body });
    const read = makeSliceReader(files.open);
    // The uploader compares this against the length it planned and names the file.
    await expect(read('file:///a.bin', 200, 100)).resolves.toHaveLength(50);
    await expect(read('file:///a.bin', 250, 100)).resolves.toHaveLength(0);
  });

  it('passes the failure of a missing file through, with no handle left open', async () => {
    const files = fakeFiles({ 'file:///a.bin': body });
    const read = makeSliceReader(files.open);
    await expect(read('file:///gone.bin', 0, 10)).rejects.toThrow('파일이 없습니다');
    expect(files.live).toBe(0);
  });

  it('closes the handle even when the read itself throws', async () => {
    let closed = false;
    const read = makeSliceReader(() => ({
      offset: 0,
      readBytes: () => {
        throw new Error('읽을 수 없습니다.');
      },
      close: () => {
        closed = true;
      },
    }));
    await expect(read('file:///a.bin', 0, 10)).rejects.toThrow('읽을 수 없습니다.');
    expect(closed).toBe(true);
  });
});
