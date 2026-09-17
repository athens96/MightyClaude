import { Component, type ReactNode } from 'react';
import { StyleSheet, Text } from 'react-native';
import Markdown from 'react-native-markdown-display';
import { colors, monoFontFamily, radius, spacing } from '@/theme';

const markdownStyles = StyleSheet.create({
  body: { color: colors.text, fontSize: 15, lineHeight: 22 },
  heading1: { color: colors.text, fontSize: 19, fontWeight: '700', marginBottom: spacing.xs },
  heading2: { color: colors.text, fontSize: 17, fontWeight: '700', marginBottom: spacing.xs },
  heading3: { color: colors.text, fontSize: 15, fontWeight: '700', marginBottom: spacing.xs },
  paragraph: { marginBottom: spacing.sm, marginTop: 0 },
  strong: { fontWeight: '700' },
  link: { color: colors.accent },
  bullet_list: { marginBottom: spacing.sm },
  ordered_list: { marginBottom: spacing.sm },
  code_inline: {
    backgroundColor: colors.surfaceRaised,
    borderRadius: radius.sm,
    color: colors.accent,
    fontFamily: monoFontFamily,
    fontSize: 13,
  },
  code_block: {
    backgroundColor: colors.surfaceRaised,
    borderColor: colors.border,
    borderRadius: radius.sm,
    borderWidth: StyleSheet.hairlineWidth,
    color: colors.text,
    fontFamily: monoFontFamily,
    fontSize: 12,
    padding: spacing.sm,
  },
  fence: {
    backgroundColor: colors.surfaceRaised,
    borderColor: colors.border,
    borderRadius: radius.sm,
    borderWidth: StyleSheet.hairlineWidth,
    color: colors.text,
    fontFamily: monoFontFamily,
    fontSize: 12,
    padding: spacing.sm,
  },
  blockquote: {
    backgroundColor: colors.surface,
    borderLeftColor: colors.accent,
    borderLeftWidth: 3,
    paddingHorizontal: spacing.sm,
  },
  hr: { backgroundColor: colors.border, height: StyleSheet.hairlineWidth },
  table: { borderColor: colors.border, borderWidth: StyleSheet.hairlineWidth },
  th: { color: colors.text, padding: spacing.xs },
  td: { color: colors.text, padding: spacing.xs },
});

const fallbackStyles = StyleSheet.create({
  text: { color: colors.text, fontSize: 15, lineHeight: 22 },
});

interface BoundaryProps {
  text: string;
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
      return <Text style={fallbackStyles.text}>{this.props.text}</Text>;
    }
    return this.props.children;
  }
}

export function AssistantMarkdown({ text }: { text: string }) {
  return (
    <MarkdownBoundary text={text}>
      <Markdown style={markdownStyles}>{text}</Markdown>
    </MarkdownBoundary>
  );
}
