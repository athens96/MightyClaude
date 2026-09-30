import { useMemo } from 'react';
import { FlatList, ScrollView, StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import {
  MAX_HIGHLIGHT_UNITS,
  WRAP_THRESHOLD,
  highlight,
  longestLine,
  sourceLanguage,
  splitLines,
  widestColumns,
  type Segment,
  type TokenKind,
} from '@/lib/highlight';
import type { FileView } from '@/lib/files';
import { t } from '@/lib/i18n';
import { monoFontFamily, spacing, useStyles, usePalette, type Palette } from '@/theme';

const LINE_HEIGHT = 18;
const FONT_SIZE = 12;
/** A monospaced 12 pt glyph's advance on both platforms, rounded up. */
const CHAR_WIDTH = 7.3;

function tokenColor(palette: Palette, kind: TokenKind): string {
  switch (kind) {
    case 'keyword':
      return palette.blockAgent;
    case 'string':
      return palette.success;
    case 'comment':
      return palette.textFaint;
    case 'number':
      return palette.warning;
  }
}

/**
 * Source as the Mac files pane shows it: monospaced, numbered, highlighted, one row
 * per line so a 512 KiB file scrolls without laying out every line. Lines scroll
 * sideways together; a line longer than the Mac's wrap threshold makes the whole
 * view wrap instead, as on the Mac.
 */
export function SourceView({ text, language }: { text: string; language: string }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { width: screenWidth } = useWindowDimensions();
  const lines = useMemo(() => splitLines(text, highlight(text, sourceLanguage(language))), [language, text]);
  const wraps = useMemo(() => longestLine(lines) > WRAP_THRESHOLD, [lines]);
  const columns = useMemo(() => (wraps ? 0 : widestColumns(lines)), [lines, wraps]);
  const gutterWidth = Math.max(2, String(lines.length).length) * CHAR_WIDTH + spacing.md;
  // A little slack over the estimate: a row that still overflows is clipped, never wrapped,
  // so every row keeps the one height the list is told.
  const contentWidth = Math.max(screenWidth, gutterWidth + columns * CHAR_WIDTH * 1.1 + spacing.lg * 2);

  const renderLine = ({ item, index }: { item: Segment[]; index: number }) => (
    <View style={styles.line}>
      <Text style={[styles.number, { width: gutterWidth }]} accessible={false}>
        {index + 1}
      </Text>
      <Text
        selectable
        numberOfLines={wraps ? undefined : 1}
        ellipsizeMode="clip"
        style={[styles.code, wraps && styles.codeWrapping]}
      >
        {item.map((segment, position) =>
          segment.kind ? (
            <Text key={position} style={{ color: tokenColor(palette, segment.kind) }}>
              {segment.text}
            </Text>
          ) : (
            segment.text
          ),
        )}
      </Text>
    </View>
  );

  const list = (
    <FlatList
      data={lines}
      accessibilityLabel={t('phone.files.source.label', { count: lines.length })}
      keyExtractor={(_, index) => String(index)}
      renderItem={renderLine}
      initialNumToRender={60}
      maxToRenderPerBatch={80}
      windowSize={11}
      getItemLayout={wraps ? undefined : (_, index) => ({ length: LINE_HEIGHT, offset: LINE_HEIGHT * index, index })}
      style={wraps ? styles.fill : { width: contentWidth }}
      contentContainerStyle={styles.listContent}
    />
  );

  return (
    <View style={styles.fill}>
      {wraps ? (
        list
      ) : (
        <ScrollView horizontal style={styles.fill} contentContainerStyle={{ width: contentWidth }}>
          {list}
        </ScrollView>
      )}
    </View>
  );
}

/** What the source header says beside the encoding, in the Mac pane's order. */
export function sourceNotices(text: string, truncated: boolean): string[] {
  const notices: string[] = [];
  if (truncated) notices.push(t('phone.files.source.truncated'));
  if (text.split(/\r\n|[\r\n\u2028\u2029]/).some((line) => line.length > WRAP_THRESHOLD)) {
    notices.push(t('phone.files.source.wrapped', { count: WRAP_THRESHOLD.toLocaleString() }));
  }
  if (text.length > MAX_HIGHLIGHT_UNITS) {
    notices.push(t('phone.files.source.highlightCapped', { count: MAX_HIGHLIGHT_UNITS.toLocaleString() }));
  }
  return notices;
}

/**
 * Every notice the file viewer's header shows for a preview. A truncated file always
 * says so, markdown too large to render included (it shows its source).
 */
export function previewNotices(view: FileView | undefined, truncated: boolean, showSource: boolean): string[] {
  const notices: string[] = [];
  if (view?.kind === 'source') notices.push(...sourceNotices(view.text, truncated));
  if (view?.kind === 'markdown') {
    if (!view.renderable) notices.push(t('phone.files.markdown.tooLarge'));
    if (!view.renderable || showSource) notices.push(...sourceNotices(view.text, truncated));
    else if (truncated) notices.push(t('phone.files.source.truncated'));
  }
  if (view?.kind === 'image') notices.push(t('phone.files.image.hint'));
  return notices;
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    fill: { flex: 1 },
    listContent: { paddingBottom: spacing.xl, paddingTop: spacing.sm },
    line: { flexDirection: 'row', minHeight: LINE_HEIGHT },
    number: {
      color: palette.textFaint,
      fontFamily: monoFontFamily,
      fontSize: FONT_SIZE,
      lineHeight: LINE_HEIGHT,
      paddingRight: spacing.sm,
      textAlign: 'right',
    },
    code: {
      color: palette.text,
      fontFamily: monoFontFamily,
      fontSize: FONT_SIZE,
      lineHeight: LINE_HEIGHT,
      paddingRight: spacing.lg,
    },
    codeWrapping: { flex: 1 },
  });
