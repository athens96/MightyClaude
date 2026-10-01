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
    info: palette.run,
    success: palette.done,
    error: palette.err,
  };

  if (toasts.length === 0) return null;

  return (
    <View pointerEvents="box-none" style={[styles.host, { bottom: insets.bottom + 64 }]}>
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
    // An ink capsule floating over the page and the tab bar; the tone is a bold dot.
    toast: {
      alignItems: 'center',
      backgroundColor: palette.bubbleUser,
      borderRadius: radius.lg,
      elevation: 4,
      flexDirection: 'row',
      gap: spacing.sm,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md,
      shadowColor: '#0F1428',
      shadowOffset: { width: 0, height: 6 },
      shadowOpacity: 0.18,
      shadowRadius: 14,
    },
    dot: { borderRadius: radius.round, height: 8, width: 8 },
    message: { color: palette.onBubbleUser, flexShrink: 1, fontSize: 14, fontWeight: '600', lineHeight: 20 },
  });
