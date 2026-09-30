import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';
import { useToastStore, type ToastTone } from '@/store/toast';

export function ToastHost() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const toasts = useToastStore((state) => state.toasts);
  const dismiss = useToastStore((state) => state.dismiss);
  const insets = useSafeAreaInsets();

  const toneColor: Record<ToastTone, string> = {
    info: palette.accent,
    success: palette.success,
    error: palette.danger,
  };

  if (toasts.length === 0) return null;

  return (
    <View pointerEvents="box-none" style={[styles.host, { bottom: insets.bottom + spacing.xl }]}>
      {toasts.map((toast) => (
        <Pressable key={toast.id} onPress={() => dismiss(toast.id)}>
          <View style={styles.toast}>
            <View style={[styles.dot, { backgroundColor: toneColor[toast.tone] }]} />
            <Text style={styles.message}>{toast.message}</Text>
          </View>
        </Pressable>
      ))}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    host: {
      alignItems: 'center',
      gap: spacing.sm,
      left: 0,
      paddingHorizontal: spacing.lg,
      position: 'absolute',
      right: 0,
    },
    // A slip of paper with a hairline edge; the tone is a dot beside the words.
    toast: {
      alignItems: 'center',
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: spacing.sm,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md,
    },
    dot: { borderRadius: radius.round, height: 6, width: 6 },
    message: { color: palette.text, flexShrink: 1, fontSize: 14, lineHeight: 20 },
  });
