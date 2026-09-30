import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ActivityIndicator, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { isAbortError } from '@/api/client';
import type { FilePreview } from '@/api/types';
import { AssistantMarkdown } from '@/components/assistant-markdown';
import { SourceView, previewNotices } from '@/components/source-view';
import { Button, Card, Chip, ErrorBanner } from '@/components/ui';
import { ZoomableImage } from '@/components/zoomable-image';
import {
  baseName,
  describeFileError,
  displayName,
  formatModified,
  unsupportedReason,
  viewFor,
} from '@/lib/files';
import { t } from '@/lib/i18n';
import { formatBytes } from '@/lib/uploads';
import { useHostClient } from '@/store/live';
import { spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

/**
 * One file of the paired Mac's workspace, read-only, previewed the way the Mac files
 * pane shows it: numbered highlighted source, rendered markdown with its source a tap
 * away, a zoomable image, or the file's details when there is nothing to preview.
 */
export default function FileScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const { hostId, workspaceId, path } = useLocalSearchParams<{
    hostId: string;
    workspaceId: string;
    path: string;
  }>();
  const client = useHostClient(hostId);
  const [preview, setPreview] = useState<FilePreview | undefined>(undefined);
  const [error, setError] = useState<string | undefined>(undefined);
  const [reload, setReload] = useState(0);
  const [showSource, setShowSource] = useState(false);
  const failed = useRef(false);
  const title = displayName(baseName(path ?? ''));

  useEffect(() => {
    if (!client || !workspaceId || !path) return undefined;
    const controller = new AbortController();
    setError(undefined);
    failed.current = false;
    client
      .filePreview(workspaceId, path, controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setPreview(data);
      })
      .catch((reason: unknown) => {
        if (controller.signal.aborted || isAbortError(reason)) return;
        failed.current = true;
        setError(describeFileError(reason));
      });
    return () => controller.abort();
  }, [client, path, reload, workspaceId]);

  // A read that failed while the tunnel was down is tried again once it is back.
  useEffect(
    () =>
      client?.onStateChange((state) => {
        if (state === 'ready' && failed.current) setReload((count) => count + 1);
      }),
    [client],
  );

  const view = useMemo(() => (preview ? viewFor(preview) : undefined), [preview]);

  const details = preview
    ? [
        formatBytes(preview.size),
        preview.encoding ? t('phone.files.encoding', { name: preview.encoding }) : undefined,
        preview.lineCount !== undefined ? t('phone.files.lines', { count: preview.lineCount }) : undefined,
        view?.kind === 'image' && preview.width && preview.height
          ? t('phone.files.image.pixels', { width: preview.width, height: preview.height })
          : undefined,
      ].filter((part): part is string => Boolean(part))
    : [];

  let body: ReactNode = null;
  if (!preview || !view) {
    body = error ? null : (
      <View style={styles.center}>
        <ActivityIndicator color={palette.accent} accessibilityLabel={t('phone.files.loading')} />
      </View>
    );
  } else if (view.kind === 'source') {
    body = <SourceView text={view.text} language={view.language} />;
  } else if (view.kind === 'markdown') {
    body =
      view.renderable && !showSource ? (
        <ScrollView contentContainerStyle={[styles.markdown, { paddingBottom: insets.bottom + spacing.xl }]}>
          <AssistantMarkdown text={view.text} selectable />
        </ScrollView>
      ) : (
        <SourceView text={view.text} language="plain" />
      );
  } else if (view.kind === 'image') {
    body = (
      <ZoomableImage
        uri={view.uri}
        pixelWidth={preview.thumbnailWidth ?? preview.width ?? 0}
        pixelHeight={preview.thumbnailHeight ?? preview.height ?? 0}
        accessibilityLabel={t('phone.files.image.label', { name: title })}
      />
    );
  } else {
    const modified = formatModified(preview.modified);
    body = (
      <View style={styles.unsupportedWrap}>
        <Card style={styles.unsupported}>
          <Text style={styles.unsupportedTitle}>{t('phone.files.unsupported.title')}</Text>
          <Text style={styles.unsupportedName}>{title}</Text>
          <Text style={styles.unsupportedReason}>{unsupportedReason(preview)}</Text>
          <Text style={styles.detail}>
            {t('phone.files.size')}: {formatBytes(preview.size)}
          </Text>
          {modified ? (
            <Text style={styles.detail}>
              {t('phone.files.modified')}: {modified}
            </Text>
          ) : null}
        </Card>
      </View>
    );
  }

  // Memoized: the source notices split the whole text into lines.
  const truncated = preview?.truncated === true;
  const notices = useMemo(() => previewNotices(view, truncated, showSource), [showSource, truncated, view]);

  return (
    <View style={styles.screen}>
      <Stack.Screen options={{ title }} />

      {error ? (
        <View style={styles.errorRow}>
          <ErrorBanner message={error} />
          <Button
            label={t('phone.files.retry')}
            compact
            onPress={() => setReload((count) => count + 1)}
            style={styles.retry}
          />
        </View>
      ) : null}

      {preview && view && view.kind !== 'unsupported' ? (
        <View style={styles.header}>
          <View style={styles.headerRow}>
            <Text style={styles.detail} accessibilityRole="text">
              {details.join(' · ')}
            </Text>
            {view.kind === 'markdown' && view.renderable ? (
              <View style={styles.toggle}>
                <Chip
                  label={t('phone.files.markdown.rendered')}
                  selected={!showSource}
                  color={palette.accent}
                  onPress={() => setShowSource(false)}
                />
                <Chip
                  label={t('phone.files.markdown.source')}
                  selected={showSource}
                  color={palette.accent}
                  onPress={() => setShowSource(true)}
                />
              </View>
            ) : null}
          </View>
          {notices.map((notice) => (
            <Text key={notice} style={styles.notice}>
              {notice}
            </Text>
          ))}
        </View>
      ) : null}

      <View style={styles.body}>{body}</View>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    center: { alignItems: 'center', flex: 1, justifyContent: 'center' },
    errorRow: { gap: spacing.sm, paddingBottom: spacing.sm },
    retry: { alignSelf: 'flex-start', marginHorizontal: spacing.lg },
    header: {
      borderBottomColor: palette.border,
      borderBottomWidth: StyleSheet.hairlineWidth,
      gap: spacing.xs,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.sm,
    },
    headerRow: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm },
    toggle: { flexDirection: 'row', gap: spacing.xs, marginLeft: 'auto' },
    detail: { color: palette.textMuted, fontSize: 12 },
    notice: { color: palette.warning, fontSize: 12 },
    body: { flex: 1 },
    markdown: { padding: spacing.lg },
    unsupportedWrap: { padding: spacing.lg },
    unsupported: { gap: spacing.sm },
    unsupportedTitle: { ...typeScale.heading, color: palette.text },
    unsupportedName: { color: palette.text, fontSize: 15 },
    unsupportedReason: { color: palette.textMuted, fontSize: 13 },
  });
