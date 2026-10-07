import { StyleSheet, Text, View } from 'react-native';
import type { MobilePermission } from '@/api/types';
import { Button, Card } from '@/components/ui';
import { t } from '@/lib/i18n';
import { monoText, radius, spacing, typeScale, useStyles, type Palette } from '@/theme';

/**
 * A tool the pane wants to run, waiting for Allow or Deny. An AskUserQuestion request is
 * not drawn here: it carries a `questionnaire` and the screen docks a
 * `QuestionnaireCard` above the composer for it instead, as the Mac does.
 */
export function PermissionCard({
  permission,
  busy,
  onDecide,
}: {
  permission: MobilePermission;
  busy: boolean;
  onDecide: (allow: boolean) => void;
}) {
  const styles = useStyles(makeStyles);

  return (
    <Card style={styles.card}>
      <Text style={styles.tool}>{permission.toolName}</Text>
      <Text style={styles.title}>{permission.title}</Text>
      {permission.headline ? <Text style={styles.headline}>{permission.headline}</Text> : null}

      {permission.fields.map((field) => (
        <View key={`${field.label}:${field.value}`} style={styles.field}>
          <Text style={styles.fieldLabel}>{field.label}</Text>
          <Text style={styles.fieldValue}>{field.value}</Text>
        </View>
      ))}

      {permission.summary ? <Text style={styles.summary}>{permission.summary}</Text> : null}

      <View style={styles.actions}>
        <Button
          label={t('permission.deny')}
          tone="neutral"
          busy={busy}
          style={styles.action}
          onPress={() => onDecide(false)}
        />
        <Button
          label={t('permission.allow')}
          tone="primary"
          busy={busy}
          disabled={!permission.canAllow}
          style={styles.action}
          onPress={() => onDecide(true)}
        />
      </View>
    </Card>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // A white card ringed in the waiting amber, the tool's name on an amber tab: the one
    // place the log asks for a decision.
    card: {
      borderColor: palette.wait,
      borderWidth: 2,
      gap: spacing.sm,
      marginVertical: spacing.sm,
    },
    tool: {
      alignSelf: 'flex-start',
      backgroundColor: palette.wait,
      borderRadius: radius.round,
      color: palette.onWait,
      fontSize: 11.5,
      fontWeight: '800',
      overflow: 'hidden',
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.xs,
    },
    title: { ...typeScale.heading, color: palette.text },
    headline: { color: palette.textMuted, fontSize: 14, lineHeight: 20 },
    field: { gap: spacing.xs },
    fieldLabel: { color: palette.textFaint, fontSize: 12 },
    fieldValue: {
      ...monoText,
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      color: palette.text,
      overflow: 'hidden',
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    summary: { color: palette.textMuted, fontSize: 14, lineHeight: 20 },
    actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.xs },
    action: { flex: 1 },
  });
