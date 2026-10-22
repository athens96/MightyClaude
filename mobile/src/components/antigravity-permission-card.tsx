import { StyleSheet, Text, View } from 'react-native';
import type { MobileAntigravityAction, MobilePermission } from '@/api/types';
import { Button, Card } from '@/components/ui';
import { antigravityKindLabel, type AntigravityDecision } from '@/lib/antigravity-permission';
import { t } from '@/lib/i18n';
import { monoText, radius, spacing, typeScale, useStyles, type Palette } from '@/theme';

/**
 * What a finished Antigravity turn was refused, in the Claude card's shape (the Mac's
 * `AntigravityPermissionCard`): every action with its kind and its command, address or
 * path, then 거절 / 항상 허용 / 이번만 허용. A card no rule can allow (`canAllow: false`)
 * offers no allow buttons, only the guidance to change the permission mode. `error` is the
 * host's reason for refusing the last answer; the card stays until the host drops it.
 */
export function AntigravityPermissionCard({
  permission,
  actions,
  busy,
  error,
  onAnswer,
}: {
  permission: MobilePermission;
  actions: readonly MobileAntigravityAction[];
  busy: boolean;
  error?: string;
  onAnswer: (decision: AntigravityDecision) => void;
}) {
  const styles = useStyles(makeStyles);

  return (
    <Card style={styles.card}>
      <View testID={`antigravity-permission-${permission.id}`} style={styles.body}>
        <Text style={styles.title}>{permission.title || t('antigravity.permission.title')}</Text>
        <Text style={styles.subtitle}>{t('antigravity.permission.subtitle')}</Text>

        {actions.map((action, index) => (
          <View key={`${index}:${action.action}`} style={styles.action}>
            <Text style={styles.kind}>{`${antigravityKindLabel(action.kind)} · ${action.displayName}`}</Text>
            {action.target !== undefined ? (
              <Text
                selectable
                style={action.kind === 'command' || action.kind === 'file' ? styles.mono : styles.target}
              >
                {action.target}
              </Text>
            ) : null}
            {action.rule !== undefined ? (
              <Text selectable style={styles.rule} numberOfLines={2}>
                {t('antigravity.permission.rule', { rule: action.rule })}
              </Text>
            ) : (
              <Text style={styles.noRule}>{t('antigravity.permission.noRule')}</Text>
            )}
          </View>
        ))}

        {error ? (
          <Text testID="antigravity-permission-error" style={styles.error}>
            {error}
          </Text>
        ) : null}

        {permission.canAllow ? <Text style={styles.note}>{t('antigravity.permission.scopeNote')}</Text> : null}

        <View style={styles.actions}>
          <Button
            label={t('antigravity.permission.deny')}
            tone="neutral"
            busy={busy}
            style={styles.button}
            onPress={() => onAnswer('deny')}
          />
          {permission.canAllow ? (
            <>
              <Button
                label={t('antigravity.permission.allowAlways')}
                tone="neutral"
                busy={busy}
                style={styles.button}
                onPress={() => onAnswer('always')}
              />
              <Button
                label={t('permission.allowOnce')}
                tone="primary"
                busy={busy}
                style={styles.button}
                onPress={() => onAnswer('once')}
              />
            </>
          ) : null}
        </View>
      </View>
    </Card>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // The permission card's amber ring: the one place the log asks for a decision.
    card: {
      borderColor: palette.wait,
      borderWidth: 2,
      marginVertical: spacing.sm,
    },
    body: { gap: spacing.sm },
    title: { ...typeScale.heading, color: palette.text },
    subtitle: { ...typeScale.caption, color: palette.textMuted },
    action: { gap: spacing.xs },
    kind: { ...typeScale.caption, color: palette.textFaint, fontWeight: '600' },
    mono: {
      ...monoText,
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      color: palette.text,
      overflow: 'hidden',
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    target: { ...typeScale.body, color: palette.text, fontWeight: '600' },
    rule: { ...monoText, ...typeScale.caption, color: palette.textMuted },
    noRule: { ...typeScale.caption, color: palette.warning },
    error: { ...typeScale.caption, color: palette.danger },
    note: { ...typeScale.caption, color: palette.textMuted },
    actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.xs },
    button: { flex: 1 },
  });
