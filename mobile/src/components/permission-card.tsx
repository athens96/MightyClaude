import { StyleSheet, Text, View } from 'react-native';
import type { MobilePermission } from '@/api/types';
import { Button, Card } from '@/components/ui';
import { monoText, spacing, useStyles, type Palette } from '@/theme';

/**
 * A tool the pane wants to run, waiting for 허용 or 거부. An AskUserQuestion request is
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
          label="거부"
          tone="neutral"
          busy={busy}
          style={styles.action}
          onPress={() => onDecide(false)}
        />
        <Button
          label="허용"
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
    card: { borderColor: palette.accent, gap: spacing.sm, marginVertical: spacing.sm },
    tool: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    title: { color: palette.text, fontSize: 16, fontWeight: '700' },
    headline: { color: palette.textMuted, fontSize: 13 },
    field: { gap: 2 },
    fieldLabel: { color: palette.textFaint, fontSize: 12 },
    fieldValue: { ...monoText, color: palette.text },
    summary: { color: palette.textMuted, fontSize: 13 },
    actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.xs },
    action: { flex: 1 },
  });
