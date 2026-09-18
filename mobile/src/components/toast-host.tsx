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
          <View style={[styles.toast, { borderColor: toneColor[toast.tone] }]}>
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
      position: 'absolute',
      right: 0,
    },
    toast: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.md,
      borderWidth: 1,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md,
    },
    message: { color: palette.text, fontSize: 14 },
  });
