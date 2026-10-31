import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import {
  SHEET_CLOSE_DELAY_MS,
  pickAfterSheet,
  pickDocuments,
  pickImages,
  recoverPendingImages,
} from '@/lib/attach-picks';
import { t } from '@/lib/i18n';

jest.mock('expo-image-picker', () => ({
  launchImageLibraryAsync: jest.fn(),
  getPendingResultAsync: jest.fn(),
}));
jest.mock('expo-document-picker', () => ({ getDocumentAsync: jest.fn() }));

// Sizes on "mockDisk" by URI; a URI not listed is a file the file system cannot open.
const mockDisk: Record<string, number> = {};
jest.mock('expo-file-system', () => ({
  File: class {
    readonly uri: string;
    constructor(uri: string) {
      this.uri = uri;
    }
    get exists() {
      return this.uri in mockDisk;
    }
    get size() {
      return mockDisk[this.uri] ?? 0;
    }
    get name() {
      return this.uri.split('/').pop() ?? '';
    }
  },
  FileMode: { ReadOnly: 'r' },
}));

const launchImages = ImagePicker.launchImageLibraryAsync as jest.Mock;
const pendingImages = ImagePicker.getPendingResultAsync as jest.Mock;
const getDocuments = DocumentPicker.getDocumentAsync as jest.Mock;

beforeEach(() => {
  jest.clearAllMocks();
  for (const key of Object.keys(mockDisk)) delete mockDisk[key];
});

describe('pickAfterSheet', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('launches the chosen picker only after the sheet has closed and its fade has passed', () => {
    const launch = jest.fn();
    const sequence = pickAfterSheet(launch);
    sequence.choose('images');
    jest.advanceTimersByTime(SHEET_CLOSE_DELAY_MS * 2);
    expect(launch).not.toHaveBeenCalled();

    sequence.sheetClosed();
    jest.advanceTimersByTime(SHEET_CLOSE_DELAY_MS - 1);
    expect(launch).not.toHaveBeenCalled();
    jest.advanceTimersByTime(1);
    expect(launch).toHaveBeenCalledTimes(1);
    expect(launch).toHaveBeenCalledWith('images');

    // The choice is spent: closing the sheet again opens nothing.
    sequence.sheetClosed();
    jest.runAllTimers();
    expect(launch).toHaveBeenCalledTimes(1);
  });

  it('opens nothing when the sheet closes without a choice', () => {
    const launch = jest.fn();
    const sequence = pickAfterSheet(launch);
    sequence.sheetClosed();
    jest.runAllTimers();
    expect(launch).not.toHaveBeenCalled();
  });

  it('opens nothing once disposed, even with a launch already waiting', () => {
    const launch = jest.fn();
    const sequence = pickAfterSheet(launch);
    sequence.choose('documents');
    sequence.sheetClosed();
    sequence.dispose();
    jest.runAllTimers();
    expect(launch).not.toHaveBeenCalled();
  });
});

describe('pickImages', () => {
  it('adds the picked photos with their size on disk', async () => {
    mockDisk['file:///cache/a.jpg'] = 1200;
    launchImages.mockResolvedValue({
      canceled: false,
      assets: [{ uri: 'file:///cache/a.jpg', fileName: 'a.jpg', fileSize: 999, mimeType: 'image/jpeg' }],
    });
    const add = jest.fn();
    const onError = jest.fn();
    await pickImages(add, onError);
    expect(add).toHaveBeenCalledWith([{ uri: 'file:///cache/a.jpg', name: 'a.jpg', size: 1200, mimeType: 'image/jpeg' }]);
    expect(onError).not.toHaveBeenCalled();
  });

  it('stays silent when the user cancels', async () => {
    launchImages.mockResolvedValue({ canceled: true, assets: null });
    const add = jest.fn();
    const onError = jest.fn();
    await pickImages(add, onError);
    expect(add).not.toHaveBeenCalled();
    expect(onError).not.toHaveBeenCalled();
  });

  it('tells the user when the picker throws', async () => {
    launchImages.mockRejectedValue(new Error('사진 선택기를 열 수 없습니다.'));
    const add = jest.fn();
    const onError = jest.fn();
    await pickImages(add, onError);
    expect(add).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledWith('사진 선택기를 열 수 없습니다.');
  });

  it('tells the user when a finished pick carries nothing', async () => {
    launchImages.mockResolvedValue({ canceled: false, assets: [] });
    const add = jest.fn();
    const onError = jest.fn();
    await pickImages(add, onError);
    expect(add).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledWith(t('phone.attachments.pickFailed'));
  });
});

describe('pickDocuments', () => {
  it('adds the picked files, falling back to the reported size off disk', async () => {
    mockDisk['file:///cache/DocumentPicker/r.pdf'] = 4000;
    getDocuments.mockResolvedValue({
      canceled: false,
      assets: [
        { uri: 'file:///cache/DocumentPicker/r.pdf', name: 'report.pdf', size: 1, mimeType: 'application/pdf' },
        { uri: 'file:///cache/DocumentPicker/n.txt', name: 'notes.txt', size: 12 },
      ],
    });
    const add = jest.fn();
    const onError = jest.fn();
    await pickDocuments(add, onError);
    expect(getDocuments).toHaveBeenCalledWith({ multiple: true, type: '*/*', copyToCacheDirectory: true });
    expect(add).toHaveBeenCalledWith([
      { uri: 'file:///cache/DocumentPicker/r.pdf', name: 'report.pdf', size: 4000, mimeType: 'application/pdf' },
      { uri: 'file:///cache/DocumentPicker/n.txt', name: 'notes.txt', size: 12 },
    ]);
    expect(onError).not.toHaveBeenCalled();
  });

  it('stays silent on cancel and tells the user on a failure', async () => {
    const add = jest.fn();
    const onError = jest.fn();
    getDocuments.mockResolvedValueOnce({ canceled: true, assets: null });
    await pickDocuments(add, onError);
    expect(onError).not.toHaveBeenCalled();

    getDocuments.mockRejectedValueOnce(new Error('문서를 복사하지 못했습니다.'));
    await pickDocuments(add, onError);
    expect(onError).toHaveBeenCalledWith('문서를 복사하지 못했습니다.');
    expect(add).not.toHaveBeenCalled();
  });
});

describe('recoverPendingImages', () => {
  it('adds photos picked while the activity was destroyed', async () => {
    mockDisk['file:///cache/ImagePicker/p.png'] = 300;
    pendingImages.mockResolvedValue({
      canceled: false,
      assets: [{ uri: 'file:///cache/ImagePicker/p.png', fileName: null, fileSize: 300, mimeType: 'image/png' }],
    });
    const add = jest.fn();
    const onError = jest.fn();
    await recoverPendingImages(add, onError);
    expect(add).toHaveBeenCalledWith([
      { uri: 'file:///cache/ImagePicker/p.png', name: 'p.png', size: 300, mimeType: 'image/png' },
    ]);
    expect(onError).not.toHaveBeenCalled();
  });

  it('does nothing when no pick was waiting', async () => {
    pendingImages.mockResolvedValue(null);
    const add = jest.fn();
    const onError = jest.fn();
    await recoverPendingImages(add, onError);
    expect(add).not.toHaveBeenCalled();
    expect(onError).not.toHaveBeenCalled();
  });

  it('tells the user when the waiting pick failed', async () => {
    pendingImages.mockResolvedValue({ code: 'ERR_FAILED_TO_READ_IMAGE', message: '사진을 읽지 못했습니다.' });
    const add = jest.fn();
    const onError = jest.fn();
    await recoverPendingImages(add, onError);
    expect(add).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledWith('사진을 읽지 못했습니다.');
  });
});
