import { useCallback, useMemo, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Button } from '@/components/ui';
import { CommandList } from '@/components/command-list';
import { Sheet } from '@/components/sheets';
import { byteLength } from '@/api/client';
import { MAX_TEXT_BYTES, type MobileCommand, type SubmitMode } from '@/api/types';
import { commandInsertion, commandQuery, filterCommands } from '@/lib/commands';
import { formatBytes, type PickedFile, type UploadProgress } from '@/lib/uploads';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

export interface ComposerAttachments {
  files: PickedFile[];
  progress: Record<number, UploadProgress>;
  uploading: boolean;
  pickImages: () => Promise<void>;
  pickDocuments: () => Promise<void>;
  remove: (uri: string) => void;
  cancel: () => void;
}

export function Composer({
  text,
  onChangeText,
  disabled,
  terminal,
  running,
  sending,
  submitModes,
  commands,
  attachments,
  onSend,
  onStop,
  onCommand,
}: {
  /** Owned by the screen, so a guided panel can send and clear it too. */
  text: string;
  onChangeText: (value: string) => void;
  disabled: boolean;
  terminal: boolean;
  running: boolean;
  sending: boolean;
  /** "submit-mode": the host tells steering and queueing apart, so both are offered. */
  submitModes: boolean;
  /** "commands": what `/` offers; empty on a host without the capability. */
  commands: MobileCommand[];
  /** "attachments": absent when the host or the pane cannot take files. */
  attachments?: ComposerAttachments;
  /** Answers whether the host took the message; a refusal leaves the text in place. */
  onSend: (text: string, mode?: SubmitMode) => Promise<boolean>;
  onStop: () => void;
  /** A command the host wants the phone to handle (rename, settings, /usage…). */
  onCommand: (command: MobileCommand) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [pickerOpen, setPickerOpen] = useState(false);
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
        onChangeText('');
        onCommand(command);
        return;
      }
      onChangeText(commandInsertion(command));
    },
    [onChangeText, onCommand],
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

  const picked = attachments?.files ?? [];
  const empty = text.trim().length === 0 && picked.length === 0;
  const blocked = disabled || tooLong || empty;
  // The host cannot fold files into a turn that is already open, so a request carrying
  // attachments is never offered as "바로 전달": it queues, or it starts the pane.
  const canSteer = picked.length === 0;

  const submit = async (mode?: SubmitMode) => {
    const value = text.trim();
    if (tooLong || (value.length === 0 && picked.length === 0)) return;
    // Only a send the host accepted empties the box: after a failure the user still has
    // what they typed — and the files they picked — and can try again.
    if (await onSend(value, mode)) onChangeText('');
  };

  return (
    <View style={styles.bar}>
      <CommandList commands={matches} onSelect={pickCommand} />
      {tooLong ? <Text style={styles.warning}>메시지가 32KiB를 넘었습니다.</Text> : null}

      {attachments && picked.length > 0 ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false}>
          <View style={styles.chips}>
            {picked.map((file, index) => {
              const progress = attachments.progress[index];
              const percent =
                progress && progress.totalBytes > 0
                  ? Math.round((progress.sentBytes / progress.totalBytes) * 100)
                  : undefined;
              return (
                <View key={file.uri} style={styles.chip}>
                  <Text numberOfLines={1} style={styles.chipName}>
                    {file.name}
                  </Text>
                  <Text style={styles.chipSize}>
                    {attachments.uploading && percent !== undefined
                      ? `${percent}%`
                      : formatBytes(file.size)}
                  </Text>
                  <Pressable
                    accessibilityLabel={`${file.name} 빼기`}
                    accessibilityRole="button"
                    disabled={attachments.uploading}
                    hitSlop={8}
                    onPress={() => attachments.remove(file.uri)}
                  >
                    <Text style={styles.chipRemove}>✕</Text>
                  </Pressable>
                </View>
              );
            })}
          </View>
        </ScrollView>
      ) : null}

      {attachments?.uploading ? (
        <View style={styles.uploadRow}>
          <Text style={styles.hint}>첨부를 보내는 중…</Text>
          <Button label="취소" tone="ghost" compact onPress={attachments.cancel} />
        </View>
      ) : running && picked.length > 0 ? (
        <Text style={styles.hint}>첨부가 있는 요청은 대기열로 들어갑니다.</Text>
      ) : null}

      <View style={styles.row}>
        {attachments ? (
          <Pressable
            accessibilityLabel="파일 첨부"
            accessibilityRole="button"
            disabled={disabled || attachments.uploading}
            onPress={() => setPickerOpen(true)}
            style={({ pressed }) => [
              styles.attach,
              (disabled || attachments.uploading) && styles.attachDisabled,
              pressed && styles.pressed,
            ]}
          >
            <Text style={styles.attachMark}>＋</Text>
          </Pressable>
        ) : null}
        <TextInput
          value={text}
          onChangeText={onChangeText}
          placeholder="메시지를 입력하세요"
          placeholderTextColor={palette.textFaint}
          style={styles.input}
          multiline
          editable={!disabled}
        />
        <View style={styles.buttons}>
          {running ? <Button label="중지" tone="danger" compact onPress={onStop} /> : null}
          {running && submitModes && canSteer ? (
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
          ) : running && submitModes ? (
            <Button
              label="다음 요청"
              tone="primary"
              compact
              busy={sending}
              disabled={blocked}
              onPress={() => void submit('queue')}
            />
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

      {attachments ? (
        <Sheet visible={pickerOpen} onClose={() => setPickerOpen(false)}>
          <Text style={styles.sheetTitle}>파일 첨부</Text>
          <Button
            label="사진 선택"
            tone="neutral"
            onPress={() => {
              setPickerOpen(false);
              void attachments.pickImages();
            }}
          />
          <Button
            label="파일 선택"
            tone="neutral"
            onPress={() => {
              setPickerOpen(false);
              void attachments.pickDocuments();
            }}
          />
          <Button label="취소" tone="ghost" onPress={() => setPickerOpen(false)} />
        </Sheet>
      ) : null}
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
    attach: {
      alignItems: 'center',
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.md,
      height: 44,
      justifyContent: 'center',
      width: 40,
    },
    attachDisabled: { opacity: 0.4 },
    attachMark: { color: palette.text, fontSize: 18 },
    chips: { flexDirection: 'row', gap: spacing.xs, paddingVertical: 2 },
    chip: {
      alignItems: 'center',
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      flexDirection: 'row',
      gap: spacing.xs,
      maxWidth: 220,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    chipName: { color: palette.text, flexShrink: 1, fontSize: 12 },
    chipSize: { color: palette.textFaint, fontSize: 11 },
    chipRemove: { color: palette.textMuted, fontSize: 12 },
    uploadRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    hint: { color: palette.textFaint, flex: 1, fontSize: 11 },
    sheetTitle: { color: palette.text, fontSize: 16, fontWeight: '700' },
    terminalNote: {
      color: palette.warning,
      fontSize: 13,
      paddingVertical: spacing.sm,
      textAlign: 'center',
    },
    warning: { color: palette.danger, fontSize: 12 },
    pressed: { opacity: 0.7 },
  });
