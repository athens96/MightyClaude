import { useState } from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import type { TerminalPrompt, TerminalPromptAnswerKind } from '@/api/types';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { isMarkedOption, terminalPromptBody, yesIsPrimary } from '@/lib/terminal-prompt';
import { cardShadow, monoText, radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The phone's copy of the Mac's terminal prompt card: a command in the pane's terminal waits
 * for input. The screen keys it by prompt id, so a new prompt starts with an empty box. A
 * password is held only in this box and cleared once sent; it is never logged or stored.
 */
export function TerminalPromptCard({
  prompt,
  busy,
  onAnswer,
}: {
  prompt: TerminalPrompt;
  busy: boolean;
  onAnswer: (answer: TerminalPromptAnswerKind, text?: string) => void;
}) {
  const styles = useStyles(makeStyles);
  const palette = usePalette();
  const [typed, setTyped] = useState('');
  const yesDefault = yesIsPrimary(prompt);
  const hasDefault = prompt.defaultYes !== undefined;
  const label = (key: 'terminalPrompt.yes' | 'terminalPrompt.no', isDefault: boolean) =>
    hasDefault && isDefault ? `${t(key)} (${t('terminalPrompt.default')})` : t(key);
  const send = () => {
    if (!terminalPromptBody(prompt, 'text', typed)) return;
    onAnswer('text', typed);
    setTyped('');
  };
  const canSend = terminalPromptBody(prompt, 'text', typed) !== null;

  return (
    <View style={styles.card} testID={`terminal-prompt-${prompt.id}`}>
      <Text style={styles.badge} accessibilityRole="header">
        {t('terminalPrompt.title')}
      </Text>
      {prompt.kind === 'choice' ? (
        <View style={styles.box} testID="terminal-prompt-text">
          <Text style={styles.mono}>{prompt.text}</Text>
          {prompt.options.map((option, index) => (
            <Text
              key={`${index}:${option}`}
              style={[styles.mono, isMarkedOption(option) && styles.marked]}
              testID={isMarkedOption(option) ? 'terminal-prompt-marked' : undefined}
            >
              {option}
            </Text>
          ))}
        </View>
      ) : (
        <View style={styles.box} testID="terminal-prompt-text">
          <Text style={styles.mono}>{prompt.text}</Text>
        </View>
      )}

      {prompt.kind === 'yesNo' ? (
        <View style={styles.row}>
          <Button
            label={label('terminalPrompt.no', hasDefault && !yesDefault)}
            tone={yesDefault ? 'neutral' : 'primary'}
            compact
            disabled={busy}
            style={styles.action}
            onPress={() => onAnswer('no')}
          />
          <Button
            label={label('terminalPrompt.yes', hasDefault && yesDefault)}
            tone={yesDefault ? 'primary' : 'neutral'}
            compact
            disabled={busy}
            style={styles.action}
            onPress={() => onAnswer('yes')}
          />
        </View>
      ) : null}

      {prompt.kind === 'secret' || prompt.kind === 'text' ? (
        <TextInput
          testID="terminal-prompt-input"
          style={styles.input}
          editable={!busy}
          secureTextEntry={prompt.kind === 'secret'}
          autoCapitalize="none"
          autoCorrect={false}
          autoComplete="off"
          value={typed}
          onChangeText={setTyped}
          onSubmitEditing={send}
          placeholder={t(prompt.kind === 'secret' ? 'terminalPrompt.secretPlaceholder' : 'terminalPrompt.textPlaceholder')}
          placeholderTextColor={palette.textFaint}
          accessibilityLabel={t(prompt.kind === 'secret' ? 'terminalPrompt.secretPlaceholder' : 'terminalPrompt.textPlaceholder')}
        />
      ) : null}
      {prompt.kind === 'secret' || prompt.kind === 'text' ? (
        <View style={styles.row}>
          {prompt.kind === 'text' ? (
            <Button
              label={t('terminalPrompt.enterOnly')}
              tone="neutral"
              compact
              disabled={busy}
              style={styles.action}
              onPress={() => onAnswer('enter')}
            />
          ) : null}
          <Button
            label={t('terminalPrompt.send')}
            tone="primary"
            compact
            busy={busy}
            disabled={!canSend}
            style={styles.action}
            onPress={send}
          />
        </View>
      ) : null}

      {prompt.kind === 'choice' ? (
        <View style={styles.row}>
          <Button label={t('terminalPrompt.up')} tone="neutral" compact disabled={busy} style={styles.action} onPress={() => onAnswer('up')} />
          <Button label={t('terminalPrompt.down')} tone="neutral" compact disabled={busy} style={styles.action} onPress={() => onAnswer('down')} />
          <Button label={t('terminalPrompt.select')} tone="primary" compact busy={busy} style={styles.action} onPress={() => onAnswer('enter')} />
        </View>
      ) : null}

      <Text style={styles.hint}>{t('terminalPrompt.help')}</Text>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // The same amber-ringed white card a question or plan docks in.
    card: {
      ...cardShadow,
      backgroundColor: palette.surface,
      borderColor: palette.wait,
      borderRadius: radius.lg,
      borderWidth: 2,
      gap: spacing.sm,
      marginHorizontal: spacing.md,
      marginTop: spacing.sm,
      padding: spacing.md,
    },
    badge: {
      alignSelf: 'flex-start',
      backgroundColor: palette.wait,
      borderRadius: radius.round,
      color: palette.onWait,
      fontSize: 11.5,
      fontWeight: '800',
      overflow: 'hidden',
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.xs,
    },
    box: {
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: 1,
      padding: spacing.sm,
    },
    mono: { ...monoText, color: palette.text },
    marked: { fontWeight: '800' },
    input: {
      ...typeScale.body,
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: 1,
      color: palette.text,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    hint: { color: palette.textMuted, fontSize: 12 },
    row: { flexDirection: 'row', gap: spacing.sm },
    action: { flex: 1 },
  });
