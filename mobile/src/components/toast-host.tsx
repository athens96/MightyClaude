import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { colors, radius, spacing } from '@/theme';
import { useToastStore, type ToastTone } from '@/store/toast';

const toneColor: Record<ToastTone, string> = {
  info: colors.accent,
  success: colors.success,
  error: colors.danger,
};

export function ToastHost() {
  const toasts = useToastStore((state) => state.toasts);
  const dismiss = useToastStore((state) => state.dismiss);
  const insets = useSafeAreaInsets();

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

const styles = StyleSheet.create({
  host: {
    alignItems: 'center',
    gap: spacing.sm,
    left: 0,
    position: 'absolute',
    right: 0,
  },
  toast: {
    backgroundColor: colors.surfaceRaised,
    borderRadius: radius.md,
    borderWidth: 1,
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.md,
  },
  message: { color: colors.text, fontSize: 14 },
});
