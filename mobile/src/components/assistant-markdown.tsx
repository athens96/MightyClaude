import { Component, type ReactNode } from 'react';
import { StyleSheet, Text, type TextStyle } from 'react-native';
import Markdown from 'react-native-markdown-display';
import { monoFontFamily, radius, spacing, useStyles, type Palette } from '@/theme';

const makeMarkdownStyles = (palette: Palette) =>
  StyleSheet.create({
    body: { color: palette.text, fontSize: 15, lineHeight: 22 },
    heading1: { color: palette.text, fontSize: 19, fontWeight: '700', marginBottom: spacing.xs },
    heading2: { color: palette.text, fontSize: 17, fontWeight: '700', marginBottom: spacing.xs },
    heading3: { color: palette.text, fontSize: 15, fontWeight: '700', marginBottom: spacing.xs },
    paragraph: { marginBottom: spacing.sm, marginTop: 0 },
    strong: { fontWeight: '700' },
    link: { color: palette.accent },
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
    table: { borderColor: palette.border, borderWidth: StyleSheet.hairlineWidth },
    th: { color: palette.text, padding: spacing.xs },
    td: { color: palette.text, padding: spacing.xs },
  });

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

export function AssistantMarkdown({ text }: { text: string }) {
  const markdownStyles = useStyles(makeMarkdownStyles);
  return (
    <MarkdownBoundary text={text} fallbackStyle={markdownStyles.body}>
      <Markdown style={markdownStyles}>{text}</Markdown>
    </MarkdownBoundary>
  );
}
