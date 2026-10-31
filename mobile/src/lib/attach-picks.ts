import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import { describeError } from '@/api/client';
import { fileNameOf, fileSizeOf } from '@/lib/file-slices';
import { t } from '@/lib/i18n';
import type { PickedFile } from '@/lib/uploads';

export type PickKind = 'images' | 'documents';

/**
 * How long the attach sheet is given to leave before a system picker opens. Android has
 * no Modal `onDismiss`, and an Activity started while the sheet's dialog is still being
 * torn down can come back without its result; this covers the dialog's fade-out.
 */
export const SHEET_CLOSE_DELAY_MS = 350;

/**
 * Holds the choice made in the attach sheet and launches that picker only once the sheet
 * has closed and its close animation has had time to finish.
 */
export function pickAfterSheet(
  launch: (kind: PickKind) => void,
  schedule: (run: () => void, ms: number) => unknown = setTimeout,
  unschedule: (timer: never) => void = clearTimeout,
) {
  let chosen: PickKind | null = null;
  let timer: unknown;
  return {
    choose(kind: PickKind) {
      chosen = kind;
    },
    sheetClosed() {
      if (chosen === null) return;
      const kind = chosen;
      chosen = null;
      timer = schedule(() => {
        timer = undefined;
        launch(kind);
      }, SHEET_CLOSE_DELAY_MS);
    },
    dispose() {
      chosen = null;
      if (timer !== undefined) unschedule(timer as never);
      timer = undefined;
    },
  };
}

type Add = (files: PickedFile[]) => void;
type OnError = (message: string) => void;

/**
 * What the host will be told about a picked file. The size on disk is what the chunks
 * will actually carry, so it wins; a picker's own number is only a fallback for a file
 * the file system would not measure, and 0 means the pick is refused by name.
 */
function pickedFile(
  uri: string,
  name: string,
  reported: number | null | undefined,
  mimeType: string | null | undefined,
): PickedFile {
  const onDisk = fileSizeOf(uri);
  const file: PickedFile = { uri, name, size: onDisk > 0 ? onDisk : (reported ?? 0) };
  if (mimeType) file.mimeType = mimeType;
  return file;
}

function imageFiles(assets: readonly ImagePicker.ImagePickerAsset[] | null | undefined): PickedFile[] {
  return (assets ?? []).map((asset, index) => {
    const name = asset.fileName?.trim() || fileNameOf(asset.uri, t('phone.attachments.photoName', { index: index + 1 }));
    return pickedFile(asset.uri, name, asset.fileSize, asset.mimeType);
  });
}

/** A pick that was not cancelled but carried nothing is a failure the user must see. */
function deliver(files: PickedFile[], add: Add, onError: OnError) {
  if (files.length === 0) onError(t('phone.attachments.pickFailed'));
  else add(files);
}

// The system photo picker (PHPicker on iOS, the Android photo picker) hands back the
// chosen items without any library permission, so none is asked for.
export async function pickImages(add: Add, onError: OnError): Promise<void> {
  try {
    const result = await ImagePicker.launchImageLibraryAsync({
      allowsMultipleSelection: true,
      mediaTypes: ['images'],
      quality: 1,
    });
    if (result.canceled) return;
    deliver(imageFiles(result.assets), add, onError);
  } catch (error) {
    onError(describeError(error));
  }
}

export async function pickDocuments(add: Add, onError: OnError): Promise<void> {
  try {
    const result = await DocumentPicker.getDocumentAsync({
      multiple: true,
      type: '*/*',
      // Without a local copy a provider-backed document has no readable `file://`.
      copyToCacheDirectory: true,
    });
    if (result.canceled) return;
    deliver(
      (result.assets ?? []).map((asset) =>
        pickedFile(asset.uri, asset.name || fileNameOf(asset.uri, t('phone.attachments.fileName')), asset.size, asset.mimeType),
      ),
      add,
      onError,
    );
  } catch (error) {
    onError(describeError(error));
  }
}

/**
 * Android may destroy the app's activity while the photo picker is up; the picked photos
 * then wait in the image picker instead of answering the launch. Asked once per mount.
 */
export async function recoverPendingImages(add: Add, onError: OnError): Promise<void> {
  try {
    const pending = await ImagePicker.getPendingResultAsync();
    if (!pending) return;
    if ('code' in pending) {
      onError(pending.message || t('phone.attachments.pickFailed'));
      return;
    }
    if (pending.canceled) return;
    deliver(imageFiles(pending.assets), add, onError);
  } catch (error) {
    onError(describeError(error));
  }
}
