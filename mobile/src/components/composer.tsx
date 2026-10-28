import { useCallback, useMemo, useState, type Ref } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import * as Haptics from 'expo-haptics';
import { Icon, type IconName } from '@/components/icons';
import { Button } from '@/components/ui';
import { CommandList } from '@/components/command-list';
import { Pending, PendingBars, usePendingShown } from '@/components/pending';
import { Sheet } from '@/components/sheets';
import { byteLength } from '@/api/client';
import { MAX_TEXT_BYTES, type MobileCommand, type SubmitMode } from '@/api/types';
import { draftIsEmpty } from '@/lib/ask-targets';
import { commandInsertion, commandQuery, filterCommands } from '@/lib/commands';
import { t } from '@/lib/i18n';
import { useBusyState } from '@/hooks/use-pending';
import type { PendingState } from '@/lib/pending';
import { busySendChoices } from '@/lib/send';
import { formatBytes, type PickedFile, type UploadProgress } from '@/lib/uploads';
import { cardShadow, radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

const IDLE_STATE: PendingState = { kind: 'idle' };

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
  ask,
  stopping,
  command,
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
  /** "submit-mode": the host takes `mode: 'queue'`, so a draft on a running pane queues. */
  submitModes: boolean;
  /** "commands": what `/` offers; empty on a host without the capability. */
  commands: MobileCommand[];
  /** "attachments": absent when the host or the pane cannot take files. */
  attachments?: ComposerAttachments;
  /**
   * The "send to" switch, present only on a pane whose host offers targets. `name` is the chosen CLI's label, absent
   * for Claude; the pill opens the choice, and while a CLI is chosen the box asks it and files cannot go along.
   * While `busy` (a choice being saved) nothing is sent.
   */
  ask?: { name?: string; busy: boolean; onOpen: () => void };
  /**
   * Hands the trimmed draft over. The screen empties the box at once and draws the request
   * (natural submit B1); a send that fails gives the words back if the box is still empty
   * and untouched. Further sends wait while `sending`.
   */
  onSend: (text: string, mode?: SubmitMode) => void;
  /** The stop call on its way: stop locks and shows the pending mark (A3). */
  stopping?: PendingState;
  /** A slash command the phone runs (`/usage`, `/help`…) on its way, with its name. */
  command?: { name: string; state: PendingState };
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
  // The pressed round button locks at once and shows the bars by the pending rule (A1).
  const sendBars = usePendingShown(useBusyState(sending));
  const stopState: PendingState = stopping ?? IDLE_STATE;
  const stopBars = usePendingShown(stopState);
  const stopLocked = stopState.kind === 'pending';

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
  const asking = ask?.name !== undefined;
  // Files stay staged while a CLI is asked but do not go, so they are no draft.
  const empty = draftIsEmpty(text, picked.length, ask?.name ?? null);
  // While a new "send to" choice is being saved, a send waits: the host could still wrap it for the old one.
  const askSaving = ask?.busy === true;
  const blocked = disabled || tooLong || empty || askSaving;
  // A draft on a running pane always queues as the next request, files or not.
  const busyChoices = busySendChoices({ submitModes });
  // While a run is busy the round buttons show only for a draft with something in it,
  // and stop shrinks beside them, as on the Mac and Windows.
  const offersDraft = running && !empty;

  const submit = (mode?: SubmitMode) => {
    const value = text.trim();
    if (tooLong || askSaving || draftIsEmpty(value, picked.length, ask?.name ?? null)) return;
    onSend(value, mode);
  };

  // Send and queue are one look: the round run-blue button, faded while it cannot go.
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
        submit(mode);
      }}
      style={({ pressed }) => [
        styles.send,
        (blocked || sending) && styles.sendDisabled,
        pressed && styles.pressed,
      ]}
    >
      {sendBars && pressedMode === mode ? (
        <PendingBars color={palette.onStatus} />
      ) : (
        <Icon name={icon} color={palette.onStatus} size={17} strokeWidth={2.8} />
      )}
    </Pressable>
  );

  return (
    <View style={styles.bar}>
      <CommandList commands={matches} onSelect={pickCommand} />
      {command ? (
        <Pending
          state={command.state}
          label={`${t('graph.state.sending')} · /${command.name}`}
          showsFailure={false}
          style={styles.commandRow}
        />
      ) : null}
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
                    disabled={attachments.uploading || sending}
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
      ) : null}

      {ask ? (
        <View style={styles.askRow}>
          <Pressable
            accessibilityLabel={t('composer.ask.menuLabel')}
            accessibilityRole="button"
            accessibilityState={{ busy: ask.busy, disabled: disabled || ask.busy }}
            accessibilityValue={{ text: ask.name ?? t('composer.ask.claude') }}
            disabled={disabled || ask.busy}
            hitSlop={6}
            onPress={ask.onOpen}
            style={({ pressed }) => [styles.askPill, asking && styles.askPillOn, pressed && styles.pressed]}
          >
            <Text style={[styles.askText, asking && styles.askTextOn]} numberOfLines={1}>
              {`${t('composer.ask.menuLabel')} · ${ask.name ?? t('composer.ask.claude')}`}
            </Text>
          </Pressable>
          {asking && attachments ? <Text style={styles.hint}>{t('composer.ask.attachmentsOff')}</Text> : null}
        </View>
      ) : null}

      <View style={styles.row}>
        {attachments ? (
          <Pressable
            accessibilityLabel={t('phone.composer.attach')}
            accessibilityRole="button"
            // The files on screen are the ones a send in flight took: none come or go meanwhile.
            disabled={disabled || attachments.uploading || sending || asking}
            onPress={() => setPickerOpen(true)}
            style={({ pressed }) => [
              styles.attach,
              (disabled || attachments.uploading || sending || asking) && styles.attachDisabled,
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
            placeholder={asking ? t('composer.ask.placeholder', { name: ask?.name ?? '' }) : t('phone.composer.placeholder')}
            placeholderTextColor={palette.textFaint}
            style={styles.input}
            multiline
            editable={!disabled}
          />
          {running ? (
            <Pressable
              accessibilityLabel={t('phone.composer.stop')}
              accessibilityRole="button"
              accessibilityState={{ busy: stopLocked, disabled: stopLocked }}
              disabled={stopLocked}
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
              {stopBars ? (
                <PendingBars color={palette.onStatus} />
              ) : (
                <Icon name="stop" color={palette.onStatus} size={offersDraft ? 16 : 18} />
              )}
            </Pressable>
          ) : null}
          {/* Stop, then queue at the far right where send sits when idle. */}
          {!running
            ? roundButton('send', t('phone.composer.send'))
            : !offersDraft
              ? null
              : busyChoices.map((choice) =>
                  choice === 'queue'
                    ? roundButton('queue', t('queue.add'), 'queue')
                    : roundButton('send', t('phone.composer.send')),
                )}
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
    askRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    askPill: {
      backgroundColor: palette.surface,
      borderRadius: radius.round,
      maxWidth: 220,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.xs,
    },
    askPillOn: { backgroundColor: palette.accentMuted },
    askText: { color: palette.textMuted, fontSize: 12, fontWeight: '600' },
    askTextOn: { color: palette.accent },
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
    commandRow: { paddingHorizontal: spacing.sm },
    pressed: { opacity: 0.6 },
  });
