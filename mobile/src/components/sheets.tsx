import { useEffect, useRef, useState, type ReactNode } from 'react';
import {
  ActivityIndicator,
  Animated,
  Easing,
  FlatList,
  KeyboardAvoidingView,
  Modal,
  PanResponder,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
  useWindowDimensions,
  type DimensionValue,
  type LayoutChangeEvent,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import * as Haptics from 'expo-haptics';
import { MAX_TITLE_LENGTH, type SettingOption } from '@/api/types';
import { Icon } from '@/components/icons';
import { PendingBars, usePendingShown } from '@/components/pending';
import { useBusyState } from '@/hooks/use-pending';
import { t } from '@/lib/i18n';
import {
  darkPalette,
  dialog,
  monoText,
  motion,
  radius,
  spacing,
  typeScale,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/*
 * The phone's popups (`.omc/plans/popup-standard.md`, Material 3): an alert is a centred
 * card with text buttons [취소] [확인] on the right; a form, a menu and a picker are a bottom
 * sheet that a tap on the backdrop, a drag down or Android's back cancels; a panel is full
 * screen with its ✕ on the left of the head. Nothing else draws a popup.
 */

/** A drag past this share of the sheet's height, or a flick down, closes it. */
const DRAG_CLOSE_SHARE = 0.25;
/** Points per millisecond: a flick down this fast closes the sheet however short it was. */
const FLING_CLOSE_VELOCITY = 0.5;
/** A finger has to move this far down before the head takes the drag from a tap. */
const DRAG_SLOP = 4;

/** Whether a drag that ended `dy` points down at `vy` closes a sheet `height` tall. */
export function sheetDragCloses(dy: number, vy: number, height: number): boolean {
  return dy > height * DRAG_CLOSE_SHARE || vy > FLING_CLOSE_VELOCITY;
}

export type DialogButtonKind = 'text' | 'danger' | 'primary';

/**
 * A popup's button, `dialog.buttonHeight` tall: `text` is the accent word (cancel, and an
 * alert's confirm), `danger` the error word, `primary` the filled accent pill a sheet's
 * main action is.
 */
export function DialogButton({
  label,
  onPress,
  kind = 'text',
  busy = false,
  disabled = false,
}: {
  label: string;
  onPress: () => void;
  kind?: DialogButtonKind;
  busy?: boolean;
  disabled?: boolean;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const inactive = disabled || busy;
  const showsBars = usePendingShown(useBusyState(busy));
  const color = kind === 'primary' ? palette.onAccent : kind === 'danger' ? palette.danger : palette.accent;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ disabled: inactive, busy }}
      disabled={inactive}
      onPress={() => {
        void Haptics.selectionAsync();
        onPress();
      }}
      style={({ pressed }) => [
        styles.button,
        kind === 'primary' && styles.buttonPrimary,
        inactive && styles.disabled,
        pressed && styles.pressed,
      ]}
    >
      {showsBars ? (
        <View accessibilityLabel={t('graph.state.sending')}>
          <PendingBars color={color} />
        </View>
      ) : (
        <Text style={[styles.buttonLabel, { color }]}>{label}</Text>
      )}
    </Pressable>
  );
}

/** The spinner and "불러오는 중…" a popup shows while its content is on its way. */
export function DialogLoading() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View accessible accessibilityLabel={t('dialog.loading')} style={styles.loading}>
      <ActivityIndicator color={palette.accent} size="small" />
      <Text style={styles.note}>{t('dialog.loading')}</Text>
    </View>
  );
}

/** The sheet rises from below when it opens and follows a finger that drags its head down. */
function useSheetSlide(visible: boolean, onClose: () => void) {
  const { height: screenHeight } = useWindowDimensions();
  const [offset] = useState(() => new Animated.Value(screenHeight));
  const sheetHeight = useRef(0);
  const close = useRef(onClose);
  close.current = onClose;

  useEffect(() => {
    if (!visible) return;
    offset.setValue(screenHeight);
    Animated.timing(offset, {
      toValue: 0,
      duration: motion.standard,
      easing: Easing.bezier(...motion.easing),
      useNativeDriver: true,
    }).start();
  }, [offset, screenHeight, visible]);

  const [pan] = useState(() => {
    const settle = () => Animated.spring(offset, { toValue: 0, useNativeDriver: true }).start();
    return PanResponder.create({
      onMoveShouldSetPanResponder: (_, move) => move.dy > DRAG_SLOP && Math.abs(move.dy) > Math.abs(move.dx),
      onPanResponderMove: (_, move) => offset.setValue(Math.max(0, move.dy)),
      onPanResponderRelease: (_, move) => {
        if (sheetDragCloses(move.dy, move.vy, sheetHeight.current)) close.current();
        else settle();
      },
      onPanResponderTerminate: settle,
    });
  });

  return {
    offset,
    pan,
    onLayout: (event: LayoutChangeEvent) => {
      sheetHeight.current = event.nativeEvent.layout.height;
    },
  };
}

const sheetMaxHeight = `${dialog.sheetMaxHeightRatio * 100}%` as DimensionValue;

/**
 * B, and every menu and picker: a sheet from the bottom with a drag handle, at most
 * `dialog.sheetMaxHeightRatio` of the screen. The backdrop, a drag down on the head and
 * Android's back all call `onClose`. `footer` holds the buttons on the right ([취소] [주
 * 동작]); `footerStart` a third action or a state on the left. `inset: false` lets rows
 * run edge to edge; `scroll: false` leaves the scrolling to a list the caller brings.
 */
export function BottomSheet({
  visible,
  onClose,
  title,
  note,
  children,
  footer,
  footerStart,
  inset = true,
  scroll = true,
}: {
  visible: boolean;
  onClose: () => void;
  title?: string;
  note?: string;
  children?: ReactNode;
  footer?: ReactNode;
  footerStart?: ReactNode;
  inset?: boolean;
  scroll?: boolean;
}) {
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const slide = useSheetSlide(visible, onClose);
  return (
    <Modal
      visible={visible}
      transparent
      animationType="fade"
      statusBarTranslucent
      navigationBarTranslucent
      onRequestClose={onClose}
    >
      <View style={styles.fill}>
        <Pressable
          testID="dialog-backdrop"
          accessibilityLabel={t('dialog.close')}
          accessibilityRole="button"
          style={styles.backdrop}
          onPress={onClose}
        />
        <KeyboardAvoidingView behavior="padding" pointerEvents="box-none" style={styles.sheetPlace}>
          <Animated.View
            onLayout={slide.onLayout}
            style={[
              styles.sheet,
              { paddingBottom: insets.bottom, transform: [{ translateY: slide.offset }] },
            ]}
          >
            <View {...slide.pan.panHandlers} style={styles.sheetHead}>
              <View style={styles.handle} />
              {title ? (
                <Text accessibilityRole="header" style={styles.title}>
                  {title}
                </Text>
              ) : null}
              {note ? <Text style={styles.note}>{note}</Text> : null}
            </View>
            {scroll ? (
              <ScrollView
                keyboardShouldPersistTaps="handled"
                style={styles.sheetBody}
                contentContainerStyle={[styles.sheetBodyContent, inset && styles.inset]}
              >
                {children}
              </ScrollView>
            ) : (
              <View style={[styles.sheetBody, inset && styles.inset]}>{children}</View>
            )}
            {footer ? (
              <View style={styles.footer}>
                <View style={styles.footerStart}>{footerStart}</View>
                {footer}
              </View>
            ) : null}
          </Animated.View>
        </KeyboardAvoidingView>
      </View>
    </Modal>
  );
}

/**
 * One row of a menu or a picker, `dialog.rowHeight` tall: the label (and a caption under
 * it) on the left, a check on the right when it is the one chosen.
 */
export function SheetRow({
  label,
  caption,
  badge,
  selected,
  danger,
  disabled,
  onPress,
}: {
  label: string;
  caption?: string;
  badge?: string;
  selected?: boolean;
  danger?: boolean;
  disabled?: boolean;
  onPress: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const color = danger ? palette.danger : selected ? palette.accent : palette.text;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: selected === true, disabled: disabled === true }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.row,
        selected && { backgroundColor: palette.accentMuted },
        disabled && styles.disabled,
        pressed && styles.pressed,
      ]}
    >
      <View style={styles.rowTexts}>
        <Text style={[styles.rowLabel, { color }]}>{label}</Text>
        {caption ? <Text style={styles.rowCaption}>{caption}</Text> : null}
      </View>
      {badge ? <Text style={styles.rowBadge}>{badge}</Text> : null}
      {selected ? <Icon name="check" color={palette.accent} size={dialog.iconSize} /> : null}
    </Pressable>
  );
}

/**
 * C: a full-screen panel. The head is `dialog.headerHeight` tall with the ✕ on the left
 * (Android's back is the same), the title in the middle and `actions` on the right.
 */
export function FullScreenPanel({
  visible,
  title,
  onClose,
  actions,
  children,
}: {
  visible: boolean;
  title: string;
  onClose: () => void;
  actions?: ReactNode;
  children?: ReactNode;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  return (
    <Modal
      visible={visible}
      animationType="slide"
      statusBarTranslucent
      navigationBarTranslucent
      onRequestClose={onClose}
    >
      <View style={[styles.panel, { paddingTop: insets.top, paddingBottom: insets.bottom }]}>
        <View style={styles.panelHead}>
          <Pressable
            accessibilityLabel={t('dialog.close')}
            accessibilityRole="button"
            onPress={onClose}
            style={({ pressed }) => [styles.close, pressed && styles.pressed]}
          >
            <Icon name="close" color={palette.text} size={dialog.closeGlyph} />
          </Pressable>
          <Text accessibilityRole="header" numberOfLines={1} style={styles.panelTitle}>
            {title}
          </Text>
          <View style={styles.panelActions}>{actions}</View>
        </View>
        <View style={styles.panelBody}>{children}</View>
      </View>
    </Modal>
  );
}

/** Option list used by the settings pickers and by slash commands that open them. It closes on a pick. */
export function PickerSheet({
  visible,
  title,
  note,
  options,
  selectedId,
  busy,
  locked,
  loading,
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
  /** The options are still on their way. */
  loading?: boolean;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <BottomSheet visible={visible} onClose={onClose} title={title} note={note} inset={false}>
      {loading ? (
        <DialogLoading />
      ) : options.length === 0 ? (
        <Text style={[styles.note, styles.inset]}>{t('phone.picker.empty')}</Text>
      ) : (
        options.map((option) => (
          <SheetRow
            key={option.id}
            label={option.label || option.id}
            badge={option.badge}
            selected={option.id === selectedId}
            disabled={locked || busy}
            onPress={() => {
              onSelect(option.id);
              onClose();
            }}
          />
        ))
      )}
    </BottomSheet>
  );
}

/** Single-line text prompt (B); used for renaming a pane. */
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
  /** An optional third action, on the left of the footer (e.g. "Back to automatic"). */
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
    <BottomSheet
      visible={visible}
      onClose={onCancel}
      title={title}
      note={description}
      footerStart={
        extraAction ? (
          <DialogButton label={extraAction.label} busy={busy} onPress={extraAction.onPress} />
        ) : undefined
      }
      footer={
        <>
          <DialogButton label={t('common.cancel')} onPress={onCancel} />
          <DialogButton
            label={confirmLabel}
            kind="primary"
            busy={busy}
            disabled={!valid}
            onPress={() => onConfirm(trimmed)}
          />
        </>
      }
    >
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
    </BottomSheet>
  );
}

/** A: a yes/no alert, a centred card. The destructive tone is used for closing a pane. */
export function ConfirmDialog({
  visible,
  title,
  description,
  cancelLabel,
  confirmLabel,
  destructive,
  busy,
  onConfirm,
  onCancel,
}: {
  visible: boolean;
  title: string;
  description: string;
  /** The cancel word; `common.cancel` when not given. */
  cancelLabel?: string;
  confirmLabel: string;
  destructive?: boolean;
  busy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const styles = useStyles(makeStyles);
  return (
    <Modal visible={visible} transparent animationType="fade" statusBarTranslucent onRequestClose={onCancel}>
      <View style={styles.alertPlace}>
        <Pressable
          testID="dialog-backdrop"
          accessibilityLabel={t('common.cancel')}
          accessibilityRole="button"
          style={styles.backdrop}
          onPress={onCancel}
        />
        <View style={styles.alert}>
          <Text accessibilityRole="header" style={styles.title}>
            {title}
          </Text>
          <Text style={styles.note}>{description}</Text>
          <View style={styles.alertActions}>
            <DialogButton label={cancelLabel ?? t('common.cancel')} onPress={onCancel} />
            <DialogButton
              label={confirmLabel}
              kind={destructive ? 'danger' : 'text'}
              busy={busy}
              onPress={onConfirm}
            />
          </View>
        </View>
      </View>
    </Modal>
  );
}

export interface SheetAction {
  id: string;
  label: string;
  /** One line under the label; the host's own help text. */
  description?: string;
}

/** Labelled actions with their help; used by "More" on the guided panel. It closes on a pick. */
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
    <BottomSheet visible={visible} onClose={onClose} title={title} note={note} inset={false} scroll={false}>
      {actions.length === 0 ? (
        <Text style={[styles.note, styles.inset]}>{t('phone.actions.empty')}</Text>
      ) : (
        // A style catalogue carries up to a hundred actions, so the rows are recycled.
        <FlatList
          data={actions}
          keyExtractor={(action) => action.id}
          style={styles.list}
          renderItem={({ item }) => (
            <SheetRow
              label={item.label}
              caption={item.description}
              disabled={busy}
              onPress={() => {
                onSelect(item.id);
                onClose();
              }}
            />
          )}
        />
      )}
    </BottomSheet>
  );
}

/** Title plus a few plain paragraphs; used for a style action's description. */
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
    <BottomSheet visible={visible} onClose={onClose} title={title}>
      {/* Two lines of an action's description can read the same; position is the key. */}
      {lines.map((line, index) => (
        <Text key={index} style={styles.note}>
          {line}
        </Text>
      ))}
    </BottomSheet>
  );
}

/** C: a scrollable monospace page; shows what `/usage` and `/help` answered. */
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
    <FullScreenPanel visible={visible} title={title} onClose={onClose}>
      <ScrollView contentContainerStyle={styles.messageBody}>
        <Text selectable style={styles.message}>
          {message}
        </Text>
      </ScrollView>
    </FullScreenPanel>
  );
}

const makeStyles = (palette: Palette) => {
  // Shadows vanish on the dark page, so there every popup carries a hairline of its own.
  const edge =
    palette === darkPalette ? { borderColor: palette.border, borderWidth: dialog.darkBorder } : null;
  return StyleSheet.create({
    fill: { flex: 1 },
    backdrop: { ...StyleSheet.absoluteFill, backgroundColor: palette.overlay },
    sheetPlace: { flex: 1, justifyContent: 'flex-end' },
    sheet: {
      ...edge,
      backgroundColor: palette.surface,
      borderTopLeftRadius: dialog.sheetRadius,
      borderTopRightRadius: dialog.sheetRadius,
      maxHeight: sheetMaxHeight,
    },
    sheetHead: { gap: spacing.sm, paddingBottom: spacing.lg, paddingHorizontal: dialog.padding },
    handle: {
      alignSelf: 'center',
      backgroundColor: palette.border,
      borderRadius: radius.round,
      height: dialog.handleHeight,
      marginBottom: spacing.lg,
      marginTop: spacing.lg,
      width: dialog.handleWidth,
    },
    sheetBody: { flexGrow: 0, flexShrink: 1 },
    sheetBodyContent: { gap: spacing.md, paddingBottom: spacing.lg },
    inset: { paddingHorizontal: dialog.padding },
    list: { flexGrow: 0 },
    footer: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: dialog.buttonGap,
      paddingHorizontal: dialog.padding,
      paddingVertical: spacing.lg,
    },
    footerStart: { alignItems: 'flex-start', flex: 1 },
    alertPlace: { alignItems: 'center', flex: 1, justifyContent: 'center', padding: dialog.padding },
    alert: {
      ...edge,
      backgroundColor: palette.surface,
      borderRadius: dialog.alertRadius,
      gap: spacing.lg,
      maxWidth: dialog.alertWidth,
      padding: dialog.padding,
      width: '100%',
    },
    alertActions: { flexDirection: 'row', gap: dialog.buttonGap, justifyContent: 'flex-end' },
    title: { ...typeScale.heading, color: palette.text },
    note: { color: palette.textMuted, fontSize: 14, lineHeight: 20 },
    loading: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.lg,
      justifyContent: 'center',
      padding: dialog.padding,
    },
    button: {
      alignItems: 'center',
      borderRadius: radius.round,
      justifyContent: 'center',
      minHeight: dialog.buttonHeight,
      paddingHorizontal: spacing.xl,
    },
    buttonPrimary: { backgroundColor: palette.accent, paddingHorizontal: dialog.padding },
    buttonLabel: { fontSize: 14, fontWeight: '600' },
    row: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.xl,
      minHeight: dialog.rowHeight,
      paddingHorizontal: dialog.padding,
      paddingVertical: spacing.md,
    },
    rowTexts: { flex: 1, gap: spacing.xs },
    rowLabel: { fontSize: 15, fontWeight: '500' },
    rowCaption: { color: palette.textMuted, fontSize: 12, lineHeight: 17 },
    rowBadge: { color: palette.warning, fontSize: 11, fontWeight: '500' },
    disabled: { opacity: 0.45 },
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
    panel: { backgroundColor: palette.background, flex: 1 },
    panelHead: {
      alignItems: 'center',
      flexDirection: 'row',
      height: dialog.headerHeight,
      paddingHorizontal: spacing.lg,
    },
    close: {
      alignItems: 'center',
      borderRadius: radius.round,
      height: dialog.closeHit,
      justifyContent: 'center',
      width: dialog.closeHit,
    },
    panelTitle: { ...typeScale.heading, color: palette.text, flex: 1, textAlign: 'center' },
    panelActions: {
      alignItems: 'center',
      flexDirection: 'row',
      justifyContent: 'flex-end',
      minWidth: dialog.closeHit,
    },
    panelBody: { flex: 1 },
    messageBody: { padding: dialog.padding },
    message: { ...monoText, color: palette.text },
  });
};
