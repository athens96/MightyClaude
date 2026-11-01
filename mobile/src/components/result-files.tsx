import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { MobileResultFile } from '@/api/types';
import { t } from '@/lib/i18n';
import { resultFileParts, shownResultFiles } from '@/lib/result-files';
import { monoText, radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * The Mac's 결과에 나온 파일 panel under a run's result card: the workspace files the
 * final result names, each a tap away from the phone's file preview. The phone is
 * narrow, so it hangs below the card rather than beside it, and a long list shows its
 * first rows until asked for the rest. Without `onOpen` (a host that serves no files)
 * the rows are only read.
 */
export function ResultFiles({ files, onOpen }: { files: readonly MobileResultFile[]; onOpen?: (file: MobileResultFile) => void }) {
  const styles = useStyles(makeStyles);
  const [all, setAll] = useState(false);
  if (files.length === 0) return null;
  const { shown, hidden } = shownResultFiles(files, all);

  return (
    <View style={styles.card}>
      <View style={styles.head}>
        <Text style={styles.title}>{t('graph.resultFiles.title')}</Text>
        <Text style={styles.count}>{files.length}</Text>
      </View>
      {shown.map((file) => {
        const { name, line, folder } = resultFileParts(file);
        const body = (
          <>
            <Text numberOfLines={1} ellipsizeMode="middle" style={styles.name}>
              {name}
              {line ? <Text style={styles.line}>{line}</Text> : null}
            </Text>
            {folder ? (
              <Text numberOfLines={1} ellipsizeMode="head" style={styles.folder}>
                {folder}
              </Text>
            ) : null}
          </>
        );
        return onOpen ? (
          <Pressable
            key={file.path}
            accessibilityRole="button"
            accessibilityLabel={t('graph.resultFiles.openFile', { path: file.path })}
            onPress={() => onOpen(file)}
            style={({ pressed }) => [styles.row, pressed && styles.pressed]}
          >
            {body}
          </Pressable>
        ) : (
          <View key={file.path} style={styles.row}>
            {body}
          </View>
        );
      })}
      {hidden > 0 || all ? (
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded: all }}
          hitSlop={8}
          onPress={() => setAll((value) => !value)}
          style={({ pressed }) => [styles.more, pressed && styles.pressed]}
        >
          <Text style={styles.toggle}>
            {all ? t('phone.blocks.resultLess') : t('phone.blocks.resultFilesMore', { count: hidden })}
          </Text>
        </Pressable>
      ) : null}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    card: {
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      gap: spacing.xs,
      marginTop: spacing.sm,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md,
    },
    head: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm, paddingBottom: spacing.xs },
    title: { color: palette.text, fontSize: 12.5, fontWeight: '800' },
    count: { color: palette.textFaint, fontSize: 11.5, fontWeight: '600' },
    row: { borderRadius: radius.lg, justifyContent: 'center', minHeight: 44, paddingVertical: spacing.xs },
    name: { color: palette.accent, fontSize: 13.5, fontWeight: '600' },
    line: { ...monoText, color: palette.textFaint, fontSize: 12 },
    folder: { ...monoText, color: palette.textFaint, fontSize: 11 },
    more: { paddingTop: spacing.xs },
    toggle: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    pressed: { opacity: 0.6 },
  });
