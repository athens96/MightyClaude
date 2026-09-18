import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import { describeError, type MobileClient } from '@/api/client';
import { fileNameOf, fileSizeOf, readFileSlice } from '@/lib/file-slices';
import {
  acceptFiles,
  uploadAttachments,
  type PickedFile,
  type UploadOutcome,
  type UploadProgress,
} from '@/lib/uploads';

export interface AttachmentsController {
  files: PickedFile[];
  /** Bytes sent for each file, by index, while an upload runs. */
  progress: Record<number, UploadProgress>;
  uploading: boolean;
  pickImages: () => Promise<void>;
  pickDocuments: () => Promise<void>;
  remove: (uri: string) => void;
  clear: () => void;
  cancel: () => void;
  /** Uploads everything; the outcome carries both the ids to submit and to unwind. */
  upload: (client: MobileClient, sessionId: string) => Promise<UploadOutcome>;
}

/**
 * The composer's attachments: picking them, keeping them inside the Mac's limits and
 * running the chunked upload. The limits are checked before anything is sent, and a
 * failed or cancelled run cancels every upload it opened and keeps the files on screen
 * so the user can try again without picking them a second time.
 */
/**
 * What the host will be told about a picked file. The size on disk is what the chunks
 * will actually carry, so it wins; a picker's own number is only a fallback for a file
 * the file system would not measure, and 0 means the pick is refused by name.
 */
function pickedFile(
  uri: string,
  name: string,
  reported: number | undefined,
  mimeType: string | undefined,
): PickedFile {
  const onDisk = fileSizeOf(uri);
  const file: PickedFile = { uri, name, size: onDisk > 0 ? onDisk : (reported ?? 0) };
  if (mimeType) file.mimeType = mimeType;
  return file;
}

export function useAttachments(onError: (message: string) => void): AttachmentsController {
  const [files, setFiles] = useState<PickedFile[]>([]);
  const [progress, setProgress] = useState<Record<number, UploadProgress>>({});
  const [uploading, setUploading] = useState(false);
  const cancelled = useRef(false);
  const mounted = useRef(true);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const add = useCallback(
    (incoming: PickedFile[]) => {
      if (incoming.length === 0) return;
      setFiles((current) => {
        const result = acceptFiles(current, incoming);
        if (result.error) onError(result.error);
        return result.files;
      });
    },
    [onError],
  );

  // The system photo picker (PHPicker on iOS, the Android photo picker) hands back the
  // chosen items without any library permission, so none is asked for.
  const pickImages = useCallback(async () => {
    try {
      const result = await ImagePicker.launchImageLibraryAsync({
        allowsMultipleSelection: true,
        mediaTypes: ['images'],
        quality: 1,
      });
      if (result.canceled) return;
      add(
        result.assets.map((asset, index) => {
          const name = asset.fileName?.trim() || fileNameOf(asset.uri, `사진 ${index + 1}`);
          return pickedFile(asset.uri, name, asset.fileSize, asset.mimeType);
        }),
      );
    } catch (error) {
      onError(describeError(error));
    }
  }, [add, onError]);

  const pickDocuments = useCallback(async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({
        multiple: true,
        type: '*/*',
        // Without a local copy a provider-backed document has no readable `file://`.
        copyToCacheDirectory: true,
      });
      if (result.canceled) return;
      add(
        result.assets.map((asset) =>
          pickedFile(asset.uri, asset.name || fileNameOf(asset.uri, '파일'), asset.size, asset.mimeType),
        ),
      );
    } catch (error) {
      onError(describeError(error));
    }
  }, [add, onError]);

  const remove = useCallback((uri: string) => {
    setFiles((current) => current.filter((file) => file.uri !== uri));
    setProgress({});
  }, []);

  const clear = useCallback(() => {
    setFiles([]);
    setProgress({});
  }, []);

  const cancel = useCallback(() => {
    cancelled.current = true;
  }, []);

  const upload = useCallback(
    async (client: MobileClient, sessionId: string) => {
      cancelled.current = false;
      setUploading(true);
      setProgress({});
      try {
        const outcome = await uploadAttachments({
          transport: {
            createUpload: (id, input) => client.createUpload(id, input),
            uploadChunk: (uploadId, index, dataBase64) =>
              client.uploadChunk(uploadId, index, dataBase64),
            completeUpload: (uploadId) => client.completeUpload(uploadId),
            cancelUpload: (uploadId) => client.cancelUpload(uploadId),
          },
          readSlice: readFileSlice,
          sessionId,
          files,
          onProgress: (entry) => setProgress((current) => ({ ...current, [entry.file]: entry })),
          isCancelled: () => cancelled.current,
        });
        return outcome;
      } finally {
        // The screen may have left while the last chunk was in flight.
        if (mounted.current) setUploading(false);
      }
    },
    [files],
  );

  return useMemo(
    () => ({
      files,
      progress,
      uploading,
      pickImages,
      pickDocuments,
      remove,
      clear,
      cancel,
      upload,
    }),
    [cancel, clear, files, pickDocuments, pickImages, progress, remove, upload, uploading],
  );
}
