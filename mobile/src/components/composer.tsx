import { useState } from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import { Button } from '@/components/ui';
import { byteLength } from '@/api/client';
import { MAX_TEXT_BYTES } from '@/api/types';
import { colors, radius, spacing } from '@/theme';

export function Composer({
  disabled,
  terminal,
  running,
  sending,
  onSend,
  onStop,
}: {
  disabled: boolean;
  terminal: boolean;
  running: boolean;
  sending: boolean;
  onSend: (text: string) => Promise<void>;
  onStop: () => void;
}) {
  const [text, setText] = useState('');
  const tooLong = byteLength(text) > MAX_TEXT_BYTES;

  if (terminal) {
    return (
      <View style={styles.bar}>
        <Text style={styles.terminalNote}>
          로컬 터미널 창은 모바일에서 명령을 보낼 수 없습니다.
        </Text>
      </View>
    );
  }

  const submit = async () => {
    const value = text.trim();
    if (!value || tooLong) return;
    await onSend(value);
    setText('');
  };

  return (
    <View style={styles.bar}>
      {tooLong ? <Text style={styles.warning}>메시지가 32KiB를 넘었습니다.</Text> : null}
      <View style={styles.row}>
        <TextInput
          value={text}
          onChangeText={setText}
          placeholder="메시지를 입력하세요"
          placeholderTextColor={colors.textFaint}
          style={styles.input}
          multiline
          editable={!disabled}
        />
        <View style={styles.buttons}>
          {running ? <Button label="중지" tone="danger" compact onPress={onStop} /> : null}
          <Button
            label="전송"
            tone="primary"
            compact
            busy={sending}
            disabled={disabled || tooLong || text.trim().length === 0}
            onPress={() => void submit()}
          />
        </View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    backgroundColor: colors.background,
    borderTopColor: colors.border,
    borderTopWidth: StyleSheet.hairlineWidth,
    gap: spacing.xs,
    paddingHorizontal: spacing.lg,
    paddingTop: spacing.sm,
  },
  row: { alignItems: 'flex-end', flexDirection: 'row', gap: spacing.sm },
  input: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: radius.md,
    borderWidth: StyleSheet.hairlineWidth,
    color: colors.text,
    flex: 1,
    fontSize: 15,
    maxHeight: 140,
    minHeight: 44,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
  },
  buttons: { gap: spacing.xs },
  terminalNote: {
    color: colors.warning,
    fontSize: 13,
    paddingVertical: spacing.sm,
    textAlign: 'center',
  },
  warning: { color: colors.danger, fontSize: 12 },
});
