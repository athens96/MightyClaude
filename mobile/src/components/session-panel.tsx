import type { ReactNode } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Icon } from '@/components/icons';
import { t } from '@/lib/i18n';
import { panelAccessibilityLabel } from '@/lib/session-panel';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

const ROW_HIT_SLOP = { top: 8, bottom: 8 };

/**
 * The session's settings and status, docked right above the composer instead of at the
 * top of the transcript, where reaching them also paged in older history. Collapsed it is
 * one row summing up the settings; a tap on the row opens the full panel and closes it
 * again. Opening it is the screen's call, so the keyboard can keep it compact. `accessory`
 * (a Mighty pane's log/blocks switch) sits at the row's end, open or not.
 */
export function SessionPanel({
  summary,
  expanded,
  maxHeight,
  onToggle,
  accessory,
  children,
}: {
  /** `Opus 5.5 · High · Auto`, see `settingsSummary`. */
  summary: string;
  /** What is drawn now, after `panelShownExpanded`. */
  expanded: boolean;
  /** The open panel scrolls past this, so it never pushes the transcript off screen. */
  maxHeight: number;
  onToggle: () => void;
  accessory?: ReactNode;
  /** What the panel opens onto; without it there is no row to tap, only the accessory. */
  children?: ReactNode;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const title = t('phone.session.panel.title');
  const body = children !== undefined && children !== null && children !== false;
  return (
    <View style={styles.wrap}>
      <View style={styles.row}>
        {body ? (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={panelAccessibilityLabel(title, summary)}
            accessibilityHint={t(expanded ? 'phone.session.panel.collapse' : 'phone.session.panel.expand')}
            accessibilityState={{ expanded }}
            hitSlop={ROW_HIT_SLOP}
            onPress={onToggle}
            style={({ pressed }) => [styles.toggle, pressed && styles.togglePressed]}
          >
            <Text numberOfLines={1} style={styles.summary}>
              {summary}
            </Text>
            <View style={expanded ? styles.chevronOpen : undefined}>
              <Icon name="chevronDown" color={palette.textMuted} size={16} />
            </View>
          </Pressable>
        ) : (
          <View style={styles.spacer} />
        )}
        {accessory ? <View style={styles.accessory}>{accessory}</View> : null}
      </View>
      {body && expanded ? (
        <ScrollView
          style={{ maxHeight }}
          contentContainerStyle={styles.body}
          keyboardShouldPersistTaps="handled"
          nestedScrollEnabled
        >
          {children}
        </ScrollView>
      ) : null}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    wrap: { paddingHorizontal: spacing.lg, paddingTop: spacing.xs },
    row: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    toggle: {
      alignItems: 'center',
      borderColor: palette.border,
      borderRadius: radius.round,
      borderWidth: StyleSheet.hairlineWidth,
      flex: 1,
      flexDirection: 'row',
      gap: spacing.xs,
      justifyContent: 'space-between',
      minHeight: 36,
      paddingHorizontal: spacing.md,
    },
    togglePressed: { backgroundColor: palette.surfaceRaised },
    spacer: { flex: 1 },
    accessory: { width: 132 },
    summary: { color: palette.textMuted, flexShrink: 1, fontSize: 12.5, fontWeight: '600' },
    chevronOpen: { transform: [{ rotate: '180deg' }] },
    body: { gap: spacing.sm, paddingTop: spacing.sm },
  });
