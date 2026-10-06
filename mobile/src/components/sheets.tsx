import { useEffect, useState, type ReactNode } from 'react';
import {
  FlatList,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { MAX_TITLE_LENGTH, type SettingOption } from '@/api/types';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import { monoText, radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

/** Shared modal shell: dimmed backdrop, a centred white card, tap outside to dismiss. */
export function Sheet({
  visible,
  onClose,
  children,
}: {
  visible: boolean;
  onClose: () => void;
  children: ReactNode;
}) {
  const styles = useStyles(makeStyles);
  return (
    <Modal visible={visible} transparent animationType="fade" onRequestClose={onClose}>
      <Pressable
        accessibilityLabel={t('common.close')}
        accessibilityRole="button"
        style={styles.backdrop}
        onPress={onClose}
      >
        <Pressable style={styles.sheet} onPress={() => undefined}>
          {children}
        </Pressable>
      </Pressable>
    </Modal>
  );
}

/** Option list used by the settings pickers and by slash commands that open them. */
export function PickerSheet({
  visible,
  title,
  note,
  options,
  selectedId,
  busy,
  locked,
  onSelect,
  onClose,
}: {
  visible: boolean;
  title: string;
  /** Explains why the picker is read-only, shown above the list. */
  note?: string;
  options: SettingOption[];
  selectedId?: string;
  busy: boolean;
  locked: boolean;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <Sheet visible={visible} onClose={onClose}>
      <Text style={styles.title}>{title}</Text>
      {note ? <Text style={styles.note}>{note}</Text> : null}
      {options.length === 0 ? (
        <Text style={styles.note}>{t('phone.picker.empty')}</Text>
      ) : (
        <ScrollView style={styles.optionList}>
          {options.map((option) => {
            const selected = option.id === selectedId;
            return (
              <Pressable
                accessibilityRole="button"
                accessibilityState={{ selected, disabled: locked || busy }}
                disabled={locked || busy}
                key={option.id}
                onPress={() => onSelect(option.id)}
                style={({ pressed }) => [
                  styles.option,
                  selected && { backgroundColor: palette.accentMuted },
                  (locked || busy) && styles.optionLocked,
                  pressed && styles.pressed,
                ]}
              >
                <Text style={[styles.optionLabel, selected && { color: palette.accent }]}>
                  {option.label || option.id}
                </Text>
                {option.badge ? <Text style={styles.optionBadge}>{option.badge}</Text> : null}
                {selected ? <Text style={styles.optionMark}>{t('accessibility.selected')}</Text> : null}
              </Pressable>
            );
          })}
        </ScrollView>
      )}
      <Button label={t('common.close')} tone="ghost" onPress={onClose} />
    </Sheet>
  );
}

/** Single-line text prompt; used for renaming a pane. */
export function PromptDialog({
  visible,
  title,
  description,
  initialValue,
  placeholder,
  confirmLabel,
  busy,
  onConfirm,
  onCancel,
  extraAction,
}: {
  visible: boolean;
  title: string;
  description?: string;
  initialValue: string;
  placeholder?: string;
  confirmLabel: string;
  busy: boolean;
  onConfirm: (value: string) => void;
  onCancel: () => void;
  /** An optional secondary action shown below the main buttons (e.g. "Back to automatic"). */
  extraAction?: { label: string; onPress: () => void };
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [value, setValue] = useState(initialValue);

  useEffect(() => {
    if (visible) setValue(initialValue);
  }, [initialValue, visible]);

  const trimmed = value.trim();
  const valid = trimmed.length > 0 && trimmed.length <= MAX_TITLE_LENGTH;

  return (
    <Sheet visible={visible} onClose={onCancel}>
      <Text style={styles.title}>{title}</Text>
      {description ? <Text style={styles.note}>{description}</Text> : null}
      <TextInput
        autoFocus
        value={value}
        onChangeText={setValue}
        placeholder={placeholder}
        placeholderTextColor={palette.textFaint}
        style={styles.input}
        maxLength={MAX_TITLE_LENGTH}
      />
      <Text style={styles.counter}>
        {trimmed.length}/{MAX_TITLE_LENGTH}
      </Text>
      <View style={styles.actions}>
        <Button label={t('common.cancel')} tone="ghost" style={styles.action} onPress={onCancel} />
        <Button
          label={confirmLabel}
          tone="primary"
          busy={busy}
          disabled={!valid}
          style={styles.action}
          onPress={() => onConfirm(trimmed)}
        />
      </View>
      {extraAction ? (
        <Button label={extraAction.label} tone="neutral" busy={busy} onPress={extraAction.onPress} />
      ) : null}
    </Sheet>
  );
}

/** Yes/no dialog; the destructive tone is used for closing a pane. */
export function ConfirmDialog({
  visible,
  title,
  description,
  confirmLabel,
  destructive,
  busy,
  onConfirm,
  onCancel,
}: {
  visible: boolean;
  title: string;
  description: string;
  confirmLabel: string;
  destructive?: boolean;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <Sheet visible={visible} onClose={onCancel}>
      <Text style={styles.title}>{title}</Text>
      <Text style={styles.note}>{description}</Text>
      <View style={styles.actions}>
        <Button label={t('common.cancel')} tone="ghost" style={styles.action} onPress={onCancel} />
        <Button
          label={confirmLabel}
          tone={destructive ? 'danger' : 'primary'}
          busy={busy}
          style={styles.action}
          onPress={onConfirm}
        />
      </View>
    </Sheet>
  );
}

export interface SheetAction {
  id: string;
  label: string;
  /** One line under the label; the host's own help text. */
  description?: string;
}

/** Labelled actions with their help; used by "More" on the guided panel. */
export function ActionListSheet({
  visible,
  title,
  note,
  actions,
  busy,
  onSelect,
  onClose,
}: {
  visible: boolean;
  title: string;
  note?: string;
  actions: SheetAction[];
  busy: boolean;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <Sheet visible={visible} onClose={onClose}>
      <Text style={styles.title}>{title}</Text>
      {note ? <Text style={styles.note}>{note}</Text> : null}
      {actions.length === 0 ? (
        <Text style={styles.note}>{t('phone.actions.empty')}</Text>
      ) : (
        // A style catalogue carries up to a hundred actions, so the rows are recycled.
        <FlatList
          data={actions}
          keyExtractor={(action) => action.id}
          style={styles.optionList}
          renderItem={({ item }) => (
            <Pressable
              accessibilityRole="button"
              accessibilityState={{ disabled: busy }}
              disabled={busy}
              onPress={() => onSelect(item.id)}
              style={({ pressed }) => [
                styles.option,
                styles.optionStacked,
                busy && styles.optionLocked,
                pressed && styles.pressed,
              ]}
            >
              <Text style={styles.optionLabel}>{item.label}</Text>
              {item.description ? (
                <Text style={styles.optionDescription}>{item.description}</Text>
              ) : null}
            </Pressable>
          )}
        />
      )}
      <Button label={t('common.close')} tone="ghost" onPress={onClose} />
    </Sheet>
  );
}

/** Title plus a few plain paragraphs; used for a Paperthin skill's description. */
export function InfoSheet({
  visible,
  title,
  lines,
  onClose,
}: {
  visible: boolean;
  title: string;
  lines: string[];
  onClose: () => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <Sheet visible={visible} onClose={onClose}>
      <Text style={styles.title}>{title}</Text>
      <ScrollView style={styles.messageBody}>
        {/* Two lines of a skill's description can read the same; position is the key. */}
        {lines.map((line, index) => (
          <Text key={index} style={styles.note}>
            {line}
          </Text>
        ))}
      </ScrollView>
      <Button label={t('common.close')} tone="ghost" onPress={onClose} />
    </Sheet>
  );
}

/** Scrollable monospace body; shows what `/usage` and `/help` answered. */
export function MessageSheet({
  visible,
  title,
  message,
  onClose,
}: {
  visible: boolean;
  title: string;
  message: string;
  onClose: () => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <Sheet visible={visible} onClose={onClose}>
      <Text style={styles.title}>{title}</Text>
      <ScrollView style={styles.messageBody}>
        <Text selectable style={styles.message}>
          {message}
        </Text>
      </ScrollView>
      <Button label={t('common.close')} tone="ghost" onPress={onClose} />
    </Sheet>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    backdrop: {
      backgroundColor: palette.overlay,
      flex: 1,
      justifyContent: 'center',
      padding: spacing.lg,
    },
    sheet: {
      backgroundColor: palette.surface,
      borderRadius: radius.hero,
      gap: spacing.md,
      padding: spacing.xl,
    },
    title: { ...typeScale.title, color: palette.text },
    note: { color: palette.textMuted, fontSize: 14, lineHeight: 20 },
    optionList: { maxHeight: 320 },
    // Options are rounded rows; the chosen one sits on the blue wash with the accent and a word.
    option: {
      alignItems: 'center',
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.md,
      flexDirection: 'row',
      gap: spacing.sm,
      marginBottom: spacing.xs,
      minHeight: 46,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    optionStacked: { alignItems: 'flex-start', flexDirection: 'column', gap: 2 },
    optionDescription: { color: palette.textMuted, fontSize: 12, lineHeight: 17 },
    optionLocked: { opacity: 0.45 },
    optionLabel: { color: palette.text, flex: 1, fontSize: 15, fontWeight: '600' },
    optionMark: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    optionBadge: { color: palette.warning, fontSize: 11, fontWeight: '500' },
    pressed: { opacity: 0.6 },
    input: {
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontSize: 15,
      minHeight: 44,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    counter: { color: palette.textFaint, fontSize: 11, textAlign: 'right' },
    actions: { flexDirection: 'row', gap: spacing.sm },
    action: { flex: 1 },
    messageBody: { maxHeight: 360 },
    message: { ...monoText, color: palette.text },
  });
