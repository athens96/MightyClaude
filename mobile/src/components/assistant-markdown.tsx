import { Component, useMemo, type ReactNode } from 'react';
import { ScrollView, StyleSheet, Text, View, type TextStyle } from 'react-native';
import Markdown, { MarkdownIt, type RenderRules } from 'react-native-markdown-display';
import { isOpenableLink, prepareMarkdown } from '@/lib/markdown';
import { headingFontFamily, monoFontFamily, radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * One parser for every result on the phone: bare URLs become links, a single
 * newline stays a line break (CLI answers are written for a terminal, not for a
 * paragraph-joining renderer), and raw HTML is shown as text rather than parsed.
 */
const parser = MarkdownIt({ html: false, linkify: true, breaks: true, typographer: false });

/** A table cell's fixed width, so the columns line up across rows inside the sideways scroller. */
const CELL_WIDTH = 140;

/**
 * The reply reads as a card of prose: generous leading, the two top heading levels in
 * bold Avenir Next, inline code on the blue wash, fenced code as an ink block, quotes set
 * off by a thin rule. The accent is kept for links.
 */
const makeMarkdownStyles = (palette: Palette, size: number) =>
  StyleSheet.create({
    body: { color: palette.text, fontSize: size, lineHeight: Math.round(size * 1.53) },
    heading1: {
      color: palette.text,
      fontFamily: headingFontFamily,
      fontSize: size + 5,
      fontWeight: '700',
      lineHeight: Math.round((size + 6) * 1.3),
      marginBottom: spacing.sm,
      marginTop: spacing.xs,
    },
    heading2: {
      color: palette.text,
      fontFamily: headingFontFamily,
      fontSize: size + 2,
      fontWeight: '700',
      lineHeight: Math.round((size + 3) * 1.3),
      marginBottom: spacing.xs,
      marginTop: spacing.xs,
    },
    heading3: {
      color: palette.text,
      fontFamily: headingFontFamily,
      fontSize: size,
      fontWeight: '700',
      marginBottom: spacing.xs,
    },
    heading4: { color: palette.text, fontSize: size, fontWeight: '600', marginBottom: spacing.xs },
    heading5: { color: palette.textMuted, fontSize: size - 1, fontWeight: '600', marginBottom: spacing.xs },
    heading6: { color: palette.textMuted, fontSize: size - 2, fontWeight: '600', marginBottom: spacing.xs },
    em: { fontStyle: 'italic' },
    s: { textDecorationLine: 'line-through' },
    paragraph: { marginBottom: spacing.sm, marginTop: 0 },
    strong: { fontWeight: '600' },
    link: { color: palette.accent, textDecorationLine: 'underline' },
    bullet_list: { marginBottom: spacing.sm },
    ordered_list: { marginBottom: spacing.sm },
    code_inline: {
      backgroundColor: palette.accentMuted,
      borderRadius: radius.sm,
      color: palette.accent,
      fontFamily: monoFontFamily,
      fontSize: size - 2,
    },
    code_block: {
      backgroundColor: palette.codeSurface,
      borderRadius: radius.md,
      borderWidth: 0,
      color: palette.codeText,
      fontFamily: monoFontFamily,
      fontSize: 12,
      lineHeight: 18,
      padding: spacing.md,
    },
    fence: {
      backgroundColor: palette.codeSurface,
      borderRadius: radius.md,
      borderWidth: 0,
      color: palette.codeText,
      fontFamily: monoFontFamily,
      fontSize: 12,
      lineHeight: 18,
      padding: spacing.md,
    },
    blockquote: {
      backgroundColor: 'transparent',
      borderLeftColor: palette.border,
      borderLeftWidth: 2,
      marginBottom: spacing.sm,
      paddingHorizontal: spacing.md,
    },
    hr: { backgroundColor: palette.border, height: StyleSheet.hairlineWidth, marginVertical: spacing.md },
    table: { borderColor: palette.border, borderRadius: radius.sm, borderWidth: StyleSheet.hairlineWidth, marginBottom: spacing.sm },
    thead: { backgroundColor: palette.surfaceRaised },
    tr: { borderBottomWidth: StyleSheet.hairlineWidth, borderColor: palette.border, flexDirection: 'row' },
    th: { color: palette.text, flex: 0, fontWeight: '600', padding: spacing.xs, width: CELL_WIDTH },
    td: { color: palette.text, flex: 0, padding: spacing.xs, width: CELL_WIDTH },
    image: { borderRadius: radius.sm, marginBottom: spacing.sm },
    image_alt: { color: palette.textMuted, fontStyle: 'italic' },
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

/**
 * An image is never fetched: its alt text stands in, as in the Mac's messages. The
 * library's own rule loads any http(s) address and turns every other source into
 * `https://<src>`, so a result or a workspace file could make the phone call out.
 */
export const imageRule: NonNullable<RenderRules[string]> = (node, _children, _parent, styles) => {
  const alt = typeof node.attributes?.alt === 'string' ? node.attributes.alt.trim() : '';
  return alt ? (
    <Text key={node.key} style={styles.image_alt}>
      {alt}
    </Text>
  ) : null;
};

/** A table wider than the phone scrolls sideways instead of squeezing every column. */
export const rules: RenderRules = {
  fence: codeRule('fence'),
  code_block: codeRule('code_block'),
  image: imageRule,
  table: (node, children, _parent, styles) => (
    <ScrollView key={node.key} horizontal showsHorizontalScrollIndicator>
      <View style={styles._VIEW_SAFE_table}>{children}</View>
    </ScrollView>
  ),
};

/** The same rules with every run of prose selectable, so a long press can copy it. */
export const selectableRules: RenderRules = {
  ...rules,
  textgroup: (node, children, _parent, styles) => (
    <Text key={node.key} selectable style={styles.textgroup}>
      {children}
    </Text>
  ),
};

interface BoundaryProps {
  text: string;
  fallbackStyle: TextStyle;
  selectable: boolean;
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
      return (
        <Text selectable={this.props.selectable} style={this.props.fallbackStyle}>
          {this.props.text}
        </Text>
      );
    }
    return this.props.children;
  }
}

/**
 * Markdown as the Mac's result wrote it: headings, lists, quotes, tables, code,
 * links, and images as their alt text. `compact` is the smaller size the Mighty blocks use;
 * `selectable` lets the prose be long-pressed and copied, as code always can.
 */
export function AssistantMarkdown({
  text,
  compact = false,
  selectable = false,
}: {
  text: string;
  compact?: boolean;
  selectable?: boolean;
}) {
  const markdownStyles = useStyles(compact ? compactStyles : regularStyles);
  const prepared = useMemo(() => prepareMarkdown(text), [text]);
  return (
    <MarkdownBoundary text={text} fallbackStyle={markdownStyles.body} selectable={selectable}>
      <Markdown
        markdownit={parser}
        rules={selectable ? selectableRules : rules}
        style={markdownStyles}
        onLinkPress={isOpenableLink}
      >
        {prepared}
      </Markdown>
    </MarkdownBoundary>
  );
}
