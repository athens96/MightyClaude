import { useCallback, useMemo, useState } from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import { Button } from '@/components/ui';
import { CommandList } from '@/components/command-list';
import { byteLength } from '@/api/client';
import { MAX_TEXT_BYTES, type MobileCommand, type SubmitMode } from '@/api/types';
import { commandInsertion, commandQuery, filterCommands } from '@/lib/commands';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

export function Composer({
  disabled,
  terminal,
  running,
  sending,
  submitModes,
  commands,
  onSend,
  onStop,
  onCommand,
}: {
  disabled: boolean;
  terminal: boolean;
  running: boolean;
  sending: boolean;
  /** "submit-mode": the host tells steering and queueing apart, so both are offered. */
  submitModes: boolean;
  /** "commands": what `/` offers; empty on a host without the capability. */
  commands: MobileCommand[];
  /** Answers whether the host took the message; a refusal leaves the text in place. */
  onSend: (text: string, mode?: SubmitMode) => Promise<boolean>;
  onStop: () => void;
  /** A command the host wants the phone to handle (rename, settings, /usage…). */
  onCommand: (command: MobileCommand) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [text, setText] = useState('');
  const tooLong = byteLength(text) > MAX_TEXT_BYTES;

  const matches = useMemo(() => {
    if (commands.length === 0) return [];
    const query = commandQuery(text);
    if (query === undefined) return [];
    return filterCommands(commands, query);
  }, [commands, text]);

  const pickCommand = useCallback(
    (command: MobileCommand) => {
      if (command.action) {
        // The phone runs it: the half-typed "/name" is not a message, so it goes away.
        setText('');
        onCommand(command);
        return;
      }
      setText(commandInsertion(command));
    },
    [onCommand],
  );

  if (terminal) {
    return (
      <View style={styles.bar}>
        <Text style={styles.terminalNote}>
          로컬 터미널 창은 모바일에서 명령을 보낼 수 없습니다.
        </Text>
      </View>
    );
  }

  const empty = text.trim().length === 0;
  const blocked = disabled || tooLong || empty;

  const submit = async (mode?: SubmitMode) => {
    const value = text.trim();
    if (!value || tooLong) return;
    // Only a send the host accepted empties the box: after a failure the user still has
    // what they typed and can try again.
    if (await onSend(value, mode)) setText('');
  };

  return (
    <View style={styles.bar}>
      <CommandList commands={matches} onSelect={pickCommand} />
      {tooLong ? <Text style={styles.warning}>메시지가 32KiB를 넘었습니다.</Text> : null}
      <View style={styles.row}>
        <TextInput
          value={text}
          onChangeText={setText}
          placeholder="메시지를 입력하세요"
          placeholderTextColor={palette.textFaint}
          style={styles.input}
          multiline
          editable={!disabled}
        />
        <View style={styles.buttons}>
          {running ? <Button label="중지" tone="danger" compact onPress={onStop} /> : null}
          {running && submitModes ? (
            <>
              <Button
                label="바로 전달"
                tone="primary"
                compact
                busy={sending}
                disabled={blocked}
                onPress={() => void submit('steer')}
              />
              <Button
                label="다음 요청"
                tone="neutral"
                compact
                disabled={blocked || sending}
                onPress={() => void submit('queue')}
              />
            </>
          ) : (
            <Button
              label="전송"
              tone="primary"
              compact
              busy={sending}
              disabled={blocked}
              onPress={() => void submit()}
            />
          )}
        </View>
      </View>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    bar: {
      backgroundColor: palette.background,
      borderTopColor: palette.border,
      borderTopWidth: StyleSheet.hairlineWidth,
      gap: spacing.xs,
      paddingHorizontal: spacing.lg,
      paddingTop: spacing.sm,
    },
    row: { alignItems: 'flex-end', flexDirection: 'row', gap: spacing.sm },
    input: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      flex: 1,
      fontSize: 15,
      maxHeight: 140,
      minHeight: 44,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    buttons: { gap: spacing.xs },
    terminalNote: {
      color: palette.warning,
      fontSize: 13,
      paddingVertical: spacing.sm,
      textAlign: 'center',
    },
    warning: { color: palette.danger, fontSize: 12 },
  });
