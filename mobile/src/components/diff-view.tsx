import { useMemo } from 'react';
import { FlatList, ScrollView, StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import type { FileChanges } from '@/api/types';
import { baseCaption, diffRows, gutterDigits, type DiffRow } from '@/lib/file-changes';
import { t } from '@/lib/i18n';
import { monoFontFamily, spacing, useStyles, type Palette } from '@/theme';

const LINE_HEIGHT = 18;
const FONT_SIZE = 12;
/** A monospaced 12 pt glyph's advance on both platforms, rounded up (source-view.tsx). */
const CHAR_WIDTH = 7.3;

/**
 * The file preview's 변경사항 (the desktop's DiffView): the base and the counts, the truncated note, then a unified diff in
 * the source view's monospace, one row per line: per hunk its `@@` head, then its lines with the old and new line numbers
 * in two gutters, added lines on the done wash and removed ones on the error wash. Rows never wrap; they scroll sideways
 * together.
 */
export function DiffView({ changes }: { changes: FileChanges }) {
  const styles = useStyles(makeStyles);
  const { width: screenWidth } = useWindowDimensions();
  const rows = useMemo(() => diffRows(changes), [changes]);
  const gutterWidth = gutterDigits(rows) * CHAR_WIDTH + spacing.md;
  const contentWidth = useMemo(() => {
    let longest = 0;
    for (const row of rows) longest = Math.max(longest, row.kind === 'noNewline' ? 32 : row.text.length);
    return Math.max(screenWidth, gutterWidth * 2 + (longest + 2) * CHAR_WIDTH * 1.1 + spacing.xl * 2);
  }, [gutterWidth, rows, screenWidth]);

  const renderRow = ({ item }: { item: DiffRow }) => {
    const tone = item.kind === 'added' ? styles.added : item.kind === 'removed' ? styles.removed : item.kind === 'hunk' ? styles.hunk : undefined;
    const ink = item.kind === 'added' ? styles.addedInk : item.kind === 'removed' ? styles.removedInk : styles.faint;
    // The +/− mark is not read; a screen reader hears the line's kind before its text instead.
    const kind = item.kind === 'added' ? t('files.changes.added') : item.kind === 'removed' ? t('files.changes.removed') : null;
    return (
      <View style={[styles.row, tone]}>
        <Text style={[styles.number, ink, { width: gutterWidth }]} accessible={false}>
          {item.old ?? ''}
        </Text>
        <Text style={[styles.number, ink, { width: gutterWidth }]} accessible={false}>
          {item.new ?? ''}
        </Text>
        <Text style={[styles.mark, ink]} accessible={false}>
          {item.kind === 'added' ? '+' : item.kind === 'removed' ? '−' : ' '}
        </Text>
        <Text
          selectable
          numberOfLines={1}
          ellipsizeMode="clip"
          accessibilityLabel={kind ? `${kind} ${item.text}` : undefined}
          style={[styles.code, item.kind === 'hunk' && styles.faint, item.kind === 'noNewline' && styles.noNewline]}
        >
          {item.kind === 'noNewline' ? `\\ ${t('files.changes.noNewline')}` : item.text}
        </Text>
      </View>
    );
  };

  return (
    <View style={styles.fill}>
      <View style={styles.caption}>
        <Text style={styles.captionText} numberOfLines={1}>
          {baseCaption(changes)}
        </Text>
        <Text style={[styles.count, styles.addedInk]}>+{changes.added}</Text>
        <Text style={[styles.count, styles.removedInk]}>−{changes.removed}</Text>
      </View>
      {changes.truncated ? <Text style={styles.truncated}>{t('files.changes.truncated')}</Text> : null}
      <ScrollView horizontal style={styles.fill} contentContainerStyle={{ width: contentWidth }}>
        <FlatList
          data={rows}
          keyExtractor={(_, index) => String(index)}
          renderItem={renderRow}
          initialNumToRender={60}
          maxToRenderPerBatch={80}
          windowSize={11}
          getItemLayout={(_, index) => ({ length: LINE_HEIGHT, offset: LINE_HEIGHT * index, index })}
          style={{ width: contentWidth }}
          contentContainerStyle={styles.listContent}
        />
      </ScrollView>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    fill: { flex: 1 },
    caption: {
      alignItems: 'center',
      borderBottomColor: palette.border,
      borderBottomWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: spacing.md,
      paddingHorizontal: spacing.xl,
      paddingVertical: spacing.sm,
    },
    captionText: { color: palette.textMuted, flex: 1, fontSize: 12 },
    count: { fontFamily: monoFontFamily, fontSize: 12 },
    truncated: { backgroundColor: palette.waitSoft, color: palette.textMuted, fontSize: 12, paddingHorizontal: spacing.xl, paddingVertical: spacing.sm },
    listContent: { paddingBottom: spacing.xxl, paddingTop: spacing.sm },
    row: { flexDirection: 'row', height: LINE_HEIGHT },
    added: { backgroundColor: palette.doneSoft },
    removed: { backgroundColor: palette.dangerSurface },
    hunk: { backgroundColor: palette.surfaceRaised },
    addedInk: { color: palette.success },
    removedInk: { color: palette.danger },
    faint: { color: palette.textFaint },
    number: { fontFamily: monoFontFamily, fontSize: FONT_SIZE, lineHeight: LINE_HEIGHT, paddingRight: spacing.sm, textAlign: 'right' },
    mark: { fontFamily: monoFontFamily, fontSize: FONT_SIZE, lineHeight: LINE_HEIGHT, width: CHAR_WIDTH * 2 },
    code: { color: palette.text, fontFamily: monoFontFamily, fontSize: FONT_SIZE, lineHeight: LINE_HEIGHT, paddingRight: spacing.lg },
    noNewline: { color: palette.textMuted, fontStyle: 'italic' },
  });
