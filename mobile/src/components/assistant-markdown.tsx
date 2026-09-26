import { Component, useMemo, type ReactNode } from 'react';
import { ScrollView, StyleSheet, Text, View, type TextStyle } from 'react-native';
import Markdown, { MarkdownIt, type RenderRules } from 'react-native-markdown-display';
import { isOpenableLink, prepareMarkdown } from '@/lib/markdown';
import { monoFontFamily, radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * One parser for every result on the phone: bare URLs become links, a single
 * newline stays a line break (CLI answers are written for a terminal, not for a
 * paragraph-joining renderer), and raw HTML is shown as text rather than parsed.
 */
const parser = MarkdownIt({ html: false, linkify: true, breaks: true, typographer: false });

/** A table cell's fixed width, so the columns line up across rows inside the sideways scroller. */
const CELL_WIDTH = 140;

const makeMarkdownStyles = (palette: Palette, size: number) =>
  StyleSheet.create({
    body: { color: palette.text, fontSize: size, lineHeight: Math.round(size * 1.45) },
    heading1: { color: palette.text, fontSize: size + 4, fontWeight: '700', marginBottom: spacing.xs },
    heading2: { color: palette.text, fontSize: size + 2, fontWeight: '700', marginBottom: spacing.xs },
    heading3: { color: palette.text, fontSize: size, fontWeight: '700', marginBottom: spacing.xs },
    heading4: { color: palette.text, fontSize: size, fontWeight: '600', marginBottom: spacing.xs },
    heading5: { color: palette.textMuted, fontSize: size - 1, fontWeight: '600', marginBottom: spacing.xs },
    heading6: { color: palette.textMuted, fontSize: size - 2, fontWeight: '600', marginBottom: spacing.xs },
    em: { fontStyle: 'italic' },
    s: { textDecorationLine: 'line-through' },
    paragraph: { marginBottom: spacing.sm, marginTop: 0 },
    strong: { fontWeight: '700' },
    link: { color: palette.accent, textDecorationLine: 'underline' },
    bullet_list: { marginBottom: spacing.sm },
    ordered_list: { marginBottom: spacing.sm },
    code_inline: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      color: palette.accent,
      fontFamily: monoFontFamily,
      fontSize: 13,
    },
    code_block: {
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontFamily: monoFontFamily,
      fontSize: 12,
      padding: spacing.sm,
    },
    fence: {
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontFamily: monoFontFamily,
      fontSize: 12,
      padding: spacing.sm,
    },
    blockquote: {
      backgroundColor: palette.surface,
      borderLeftColor: palette.accent,
      borderLeftWidth: 3,
      paddingHorizontal: spacing.sm,
    },
    hr: { backgroundColor: palette.border, height: StyleSheet.hairlineWidth },
    table: { borderColor: palette.border, borderRadius: radius.sm, borderWidth: StyleSheet.hairlineWidth, marginBottom: spacing.sm },
    thead: { backgroundColor: palette.surfaceRaised },
    tr: { borderBottomWidth: StyleSheet.hairlineWidth, borderColor: palette.border, flexDirection: 'row' },
    th: { color: palette.text, flex: 0, fontWeight: '700', padding: spacing.xs, width: CELL_WIDTH },
    td: { color: palette.text, flex: 0, padding: spacing.xs, width: CELL_WIDTH },
    image: { borderRadius: radius.sm, marginBottom: spacing.sm },
  });

// Two fixed factories: `useStyles` caches by factory identity.
const regularStyles = (palette: Palette) => makeMarkdownStyles(palette, 15);
const compactStyles = (palette: Palette) => makeMarkdownStyles(palette, 13);

/** A code block keeps its lines: it scrolls sideways instead of wrapping. */
function codeRule(key: 'fence' | 'code_block'): RenderRules[string] {
  return (node, _children, _parent, styles, inheritedStyles = {}) => {
    const content = typeof node.content === 'string' ? node.content.replace(/\n$/, '') : '';
    return (
      <ScrollView key={node.key} horizontal showsHorizontalScrollIndicator={false} style={{ marginBottom: spacing.sm }}>
        <Text selectable style={[inheritedStyles, styles[key]]}>
          {content}
        </Text>
      </ScrollView>
    );
  };
}

/** A table wider than the phone scrolls sideways instead of squeezing every column. */
const rules: RenderRules = {
  fence: codeRule('fence'),
  code_block: codeRule('code_block'),
  table: (node, children, _parent, styles) => (
    <ScrollView key={node.key} horizontal showsHorizontalScrollIndicator>
      <View style={styles._VIEW_SAFE_table}>{children}</View>
    </ScrollView>
  ),
};

interface BoundaryProps {
  text: string;
  fallbackStyle: TextStyle;
  children: ReactNode;
}

/** Renders markdown, degrading to plain text if the renderer throws on odd input. */
class MarkdownBoundary extends Component<BoundaryProps, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError(): { failed: boolean } {
    return { failed: true };
  }

  render(): ReactNode {
    if (this.state.failed) {
      return <Text style={this.props.fallbackStyle}>{this.props.text}</Text>;
    }
    return this.props.children;
  }
}

/**
 * Markdown as the Mac's result wrote it: headings, lists, quotes, tables, code,
 * links and images. `compact` is the smaller size the Mighty blocks use.
 */
export function AssistantMarkdown({ text, compact = false }: { text: string; compact?: boolean }) {
  const markdownStyles = useStyles(compact ? compactStyles : regularStyles);
  const prepared = useMemo(() => prepareMarkdown(text), [text]);
  return (
    <MarkdownBoundary text={text} fallbackStyle={markdownStyles.body}>
      <Markdown markdownit={parser} rules={rules} style={markdownStyles} onLinkPress={isOpenableLink}>
        {prepared}
      </Markdown>
    </MarkdownBoundary>
  );
}
