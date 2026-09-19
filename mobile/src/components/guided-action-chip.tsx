import { ActivityIndicator, Pressable, StyleSheet, Text } from 'react-native';
import type { StyleAction } from '@/api/types';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/** The Mac's `readOnly`/`userInvoked` marks; this app has no SF Symbol renderer. */
const FLAG_MARKS: Record<string, string> = {
  userInvoked: '👤',
  readOnly: '👁',
};

/**
 * One action of a guided style. The chip carries the manifest's own glyph and title —
 * plain text, never markdown (contract 4.6) — and says by being unpressable what the
 * host would otherwise answer a moment later: a style that is not ready yet, an action
 * already in flight, or one whose prompt needs text the composer does not have.
 */
export function GuidedActionChip({
  action,
  prominent,
  recommended,
  tint,
  disabled,
  busy,
  onPress,
  onLongPress,
}: {
  action: StyleAction;
  /** The step the host puts first; drawn filled. */
  prominent: boolean;
  recommended: boolean;
  tint: string;
  disabled: boolean;
  busy: boolean;
  onPress: () => void;
  onLongPress: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const marks = action.flags.map((flag) => FLAG_MARKS[flag]).filter((mark) => mark !== undefined);
  const label = action.help ? `${action.title} · ${action.help}` : action.title;

  return (
    <Pressable
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ busy, disabled }}
      disabled={disabled || busy}
      onLongPress={onLongPress}
      onPress={onPress}
      style={({ pressed }) => [
        styles.chip,
        { borderColor: recommended || prominent || busy ? tint : palette.border },
        prominent && { backgroundColor: tint },
        busy && !prominent && { backgroundColor: palette.accentMuted },
        disabled && styles.locked,
        pressed && styles.pressed,
      ]}
    >
      {busy ? (
        <ActivityIndicator color={prominent ? palette.onAccent : tint} size="small" />
      ) : (
        <>
          {action.glyph ? <Text style={styles.glyph}>{action.glyph}</Text> : null}
          <Text
            style={[
              styles.title,
              { color: prominent ? palette.onAccent : recommended ? tint : palette.text },
            ]}
          >
            {action.title}
          </Text>
          {marks.map((mark) => (
            <Text key={mark} style={styles.mark}>
              {mark}
            </Text>
          ))}
        </>
      )}
    </Pressable>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    chip: {
      alignItems: 'center',
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: 4,
      minHeight: 34,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.xs,
    },
    glyph: { fontSize: 12 },
    title: { fontSize: 13, fontWeight: '600' },
    mark: { color: palette.textFaint, fontSize: 10 },
    locked: { opacity: 0.45 },
    pressed: { opacity: 0.7 },
  });
