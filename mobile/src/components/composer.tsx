import { useCallback, useMemo, useState, type Ref } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import * as Haptics from 'expo-haptics';
import { Icon, type IconName } from '@/components/icons';
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
  // Which round button sent last, so only that one spins while the host answers.
  const [pressedMode, setPressedMode] = useState<SubmitMode | undefined>(undefined);
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
          {t('phone.composer.terminalNote')}
        </Text>
      </View>
    );
  }

  const picked = attachments?.files ?? [];
  const empty = text.trim().length === 0 && picked.length === 0;
  const blocked = disabled || tooLong || empty;
  // The host cannot fold files into a turn that is already open, so a request carrying
  // attachments is never offered as "send now": it queues, or it starts the pane.
  const canSteer = picked.length === 0;
  // While a run is busy the round buttons show only for a draft with something in it,
  // and stop shrinks beside them, as on the Mac and Windows.
  const offersDraft = running && !empty;

  const submit = async (mode?: SubmitMode) => {
    const value = text.trim();
    if (tooLong || (value.length === 0 && picked.length === 0)) return;
    // Only a send the host accepted empties the box: after a failure the user still has
    // what they typed — and the files they picked — and can try again.
    if (await onSend(value, mode)) onChangeText('');
  };

  // Send, steer and queue are one look: the round run-blue button, faded while it cannot go.
  const roundButton = (icon: IconName, label: string, mode?: SubmitMode) => (
    <Pressable
      key={mode ?? 'send'}
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ disabled: blocked || sending, busy: sending && pressedMode === mode }}
      disabled={blocked || sending}
      hitSlop={6}
      onPress={() => {
        void Haptics.selectionAsync();
        setPressedMode(mode);
        void submit(mode);
      }}
      style={({ pressed }) => [
        styles.send,
        (blocked || sending) && styles.sendDisabled,
        pressed && styles.pressed,
      ]}
    >
      {sending && pressedMode === mode ? (
        <ActivityIndicator color={palette.onStatus} size="small" />
      ) : (
        <Icon name={icon} color={palette.onStatus} size={17} strokeWidth={2.8} />
      )}
    </Pressable>
  );

  return (
    <View style={styles.bar}>
      <CommandList commands={matches} onSelect={pickCommand} />
      {tooLong ? <Text style={styles.warning}>{t('phone.composer.tooLong')}</Text> : null}

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
                    accessibilityLabel={t('phone.composer.removeAttachment', { name: file.name })}
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
          <Text style={styles.hint}>{t('phone.composer.uploading')}</Text>
          <Button label={t('common.cancel')} tone="ghost" compact onPress={attachments.cancel} />
        </View>
      ) : running && picked.length > 0 ? (
        <Text style={styles.hint}>{t('phone.composer.attachmentsQueue')}</Text>
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
            placeholder={t('phone.composer.placeholder')}
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
              style={({ pressed }) => [
                styles.stop,
                offersDraft && styles.stopCompact,
                pressed && styles.pressed,
              ]}
            >
              <Icon name="stop" color={palette.onStatus} size={offersDraft ? 16 : 18} />
            </Pressable>
          ) : null}
          {/* Stop, then steer, then queue at the far right where send sits when idle. */}
          {!running
            ? roundButton('send', t('phone.composer.send'))
            : !offersDraft
              ? null
              : submitModes
                ? [
                    canSteer ? roundButton('steer', t('phone.composer.steer'), 'steer') : null,
                    roundButton('queue', t('queue.add'), 'queue'),
                  ]
                : roundButton('send', t('phone.composer.send'))}
        </View>
      </View>

      {attachments ? (
        <Sheet visible={pickerOpen} onClose={() => setPickerOpen(false)}>
          <Text style={styles.sheetTitle}>{t('phone.composer.attach')}</Text>
          <Button
            label={t('phone.composer.pickPhotos')}
            tone="neutral"
            onPress={() => {
              setPickerOpen(false);
              void attachments.pickImages();
            }}
          />
          <Button
            label={t('phone.composer.pickFiles')}
            tone="neutral"
            onPress={() => {
              setPickerOpen(false);
              void attachments.pickDocuments();
            }}
          />
          <Button label={t('common.cancel')} tone="ghost" onPress={() => setPickerOpen(false)} />
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
      paddingHorizontal: spacing.lg,
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
      gap: spacing.md,
      minHeight: 44,
      paddingBottom: spacing.sm,
      paddingLeft: spacing.lg,
      paddingRight: spacing.sm,
      paddingTop: spacing.sm,
    },
    input: {
      color: palette.text,
      flex: 1,
      fontSize: 15,
      lineHeight: 21,
      maxHeight: 140,
      minHeight: 34,
      paddingBottom: spacing.md,
      paddingTop: spacing.md,
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
    // 4 under the round buttons, centred on them; hitSlop keeps the touch target over 40.
    stopCompact: { borderRadius: 9, height: 30, marginBottom: spacing.xs, width: 30 },
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
    chips: { flexDirection: 'row', gap: spacing.xs, paddingVertical: spacing.xs },
    chip: {
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.round,
      flexDirection: 'row',
      gap: spacing.xs,
      maxWidth: 220,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
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
