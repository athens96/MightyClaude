import { useCallback, useMemo, useState, type Ref } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import * as Haptics from 'expo-haptics';
import { Icon } from '@/components/icons';
import { Button } from '@/components/ui';
import { CommandList } from '@/components/command-list';
import { Sheet } from '@/components/sheets';
import { byteLength } from '@/api/client';
import { MAX_TEXT_BYTES, type MobileCommand, type SubmitMode } from '@/api/types';
import { commandInsertion, commandQuery, filterCommands } from '@/lib/commands';
import { t } from '@/lib/i18n';
import { formatBytes, type PickedFile, type UploadProgress } from '@/lib/uploads';
import { cardShadow, radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

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
  inputRef,
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
  /** Lets the screen focus the box after filling it, e.g. from a `next:` suggestion. */
  inputRef?: Ref<TextInput>;
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

      {running && submitModes ? (
        // While a run is going the message can join it or wait for the next turn; both
        // ride above the field, so the field itself keeps only 중지.
        <View style={styles.modeRow}>
          {canSteer ? (
            <Button
              label="바로 전달"
              tone="primary"
              compact
              busy={sending}
              disabled={blocked}
              onPress={() => void submit('steer')}
            />
          ) : null}
          <Button
            label="다음 요청"
            tone={canSteer ? 'neutral' : 'primary'}
            compact
            busy={!canSteer && sending}
            disabled={blocked || (canSteer && sending)}
            onPress={() => void submit('queue')}
          />
        </View>
      ) : null}

      <View style={styles.row}>
        {attachments ? (
          <Pressable
            accessibilityLabel={t('phone.composer.attach')}
            accessibilityRole="button"
            disabled={disabled || attachments.uploading}
            onPress={() => setPickerOpen(true)}
            style={({ pressed }) => [
              styles.attach,
              (disabled || attachments.uploading) && styles.attachDisabled,
              pressed && styles.pressed,
            ]}
          >
            <Icon name="plus" color={palette.textMuted} size={20} strokeWidth={2.4} />
          </Pressable>
        ) : null}
        <View style={styles.field}>
          <TextInput
            ref={inputRef}
            value={text}
            onChangeText={onChangeText}
            placeholder="메시지를 입력하세요"
            placeholderTextColor={palette.textFaint}
            style={styles.input}
            multiline
            editable={!disabled}
          />
          {running ? (
            <Pressable
              accessibilityLabel={t('phone.composer.stop')}
              accessibilityRole="button"
              hitSlop={6}
              onPress={() => {
                void Haptics.selectionAsync();
                onStop();
              }}
              style={({ pressed }) => [styles.stop, pressed && styles.pressed]}
            >
              <Icon name="stop" color={palette.onStatus} size={18} />
            </Pressable>
          ) : null}
          {running && submitModes ? null : (
            <Pressable
              accessibilityLabel={t('phone.composer.send')}
              accessibilityRole="button"
              accessibilityState={{ disabled: blocked || sending, busy: sending }}
              disabled={blocked || sending}
              hitSlop={6}
              onPress={() => {
                void Haptics.selectionAsync();
                void submit();
              }}
              style={({ pressed }) => [
                styles.send,
                (blocked || sending) && styles.sendDisabled,
                pressed && styles.pressed,
              ]}
            >
              {sending ? (
                <ActivityIndicator color={palette.onStatus} size="small" />
              ) : (
                <Icon name="send" color={palette.onStatus} size={17} strokeWidth={2.8} />
              )}
            </Pressable>
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
      gap: spacing.xs,
      paddingHorizontal: spacing.md + 2,
      paddingTop: spacing.sm,
    },
    row: { alignItems: 'flex-end', flexDirection: 'row', gap: spacing.sm },
    // A white rounded field that holds its own round blue send button.
    field: {
      ...cardShadow,
      alignItems: 'flex-end',
      backgroundColor: palette.surface,
      borderRadius: 22,
      flex: 1,
      flexDirection: 'row',
      gap: 6,
      minHeight: 44,
      paddingBottom: 5,
      paddingLeft: spacing.md + 2,
      paddingRight: 5,
      paddingTop: 5,
    },
    input: {
      color: palette.text,
      flex: 1,
      fontSize: 15,
      lineHeight: 21,
      maxHeight: 140,
      minHeight: 34,
      paddingBottom: 6,
      paddingTop: 6,
    },
    send: {
      alignItems: 'center',
      backgroundColor: palette.run,
      borderRadius: radius.round,
      height: 34,
      justifyContent: 'center',
      width: 34,
    },
    sendDisabled: { opacity: 0.35 },
    stop: {
      alignItems: 'center',
      backgroundColor: palette.err,
      borderRadius: 10,
      height: 34,
      justifyContent: 'center',
      width: 34,
    },
    modeRow: { flexDirection: 'row', gap: spacing.xs, justifyContent: 'flex-end' },
    attach: {
      ...cardShadow,
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.round,
      height: 44,
      justifyContent: 'center',
      width: 44,
    },
    attachDisabled: { opacity: 0.4 },
    chips: { flexDirection: 'row', gap: spacing.xs, paddingVertical: 2 },
    chip: {
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.round,
      flexDirection: 'row',
      gap: spacing.xs,
      maxWidth: 220,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.xs + 1,
    },
    chipName: { color: palette.text, flexShrink: 1, fontSize: 12, fontWeight: '600' },
    chipSize: { color: palette.textFaint, fontSize: 11 },
    chipRemove: { color: palette.textMuted, fontSize: 12 },
    uploadRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    hint: { color: palette.textFaint, flex: 1, fontSize: 11 },
    sheetTitle: { ...typeScale.title, color: palette.text },
    terminalNote: {
      color: palette.warning,
      fontSize: 13,
      fontWeight: '600',
      paddingVertical: spacing.sm,
      textAlign: 'center',
    },
    warning: { color: palette.danger, fontSize: 12 },
    pressed: { opacity: 0.6 },
  });
