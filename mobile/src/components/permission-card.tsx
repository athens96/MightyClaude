import { useCallback, useState } from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import type { MobilePermission, QuestionAnswers } from '@/api/types';
import { Button, Card, Chip } from '@/components/ui';
import { colors, monoText, radius, spacing } from '@/theme';

interface Selection {
  options: string[];
  customText: string;
}

function toggle(current: string[], label: string, multiSelect: boolean): string[] {
  if (!multiSelect) return current.includes(label) ? [] : [label];
  return current.includes(label)
    ? current.filter((entry) => entry !== label)
    : [...current, label];
}

export function PermissionCard({
  permission,
  busy,
  onDecide,
  onAnswer,
}: {
  permission: MobilePermission;
  busy: boolean;
  onDecide: (allow: boolean) => void;
  onAnswer: (answers: QuestionAnswers) => void;
}) {
  const [selections, setSelections] = useState<Record<string, Selection>>({});

  const update = useCallback((question: string, next: Partial<Selection>) => {
    setSelections((prev) => {
      const current = prev[question] ?? { options: [], customText: '' };
      return { ...prev, [question]: { ...current, ...next } };
    });
  }, []);

  const submitAnswers = useCallback(() => {
    const questions = permission.questionnaire?.questions ?? [];
    const answers: QuestionAnswers = {};
    for (const question of questions) {
      const selection = selections[question.question] ?? { options: [], customText: '' };
      const custom = selection.customText.trim();
      answers[question.question] = {
        selectedOptions: selection.options,
        ...(custom.length > 0 ? { customText: custom } : {}),
      };
    }
    onAnswer(answers);
  }, [onAnswer, permission.questionnaire, selections]);

  const questions = permission.questionnaire?.questions ?? [];
  const hasAnswer = questions.some((question) => {
    const selection = selections[question.question];
    return Boolean(selection && (selection.options.length > 0 || selection.customText.trim()));
  });

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

      {questions.length > 0 ? (
        <View style={styles.questions}>
          {questions.map((question) => {
            const selection = selections[question.question] ?? { options: [], customText: '' };
            return (
              <View key={question.question} style={styles.question}>
                <Text style={styles.questionHeader}>{question.header}</Text>
                <Text style={styles.questionText}>{question.question}</Text>
                {question.multiSelect ? (
                  <Text style={styles.multiHint}>여러 개 선택할 수 있습니다.</Text>
                ) : null}
                <View style={styles.options}>
                  {question.options.map((option) => (
                    <Chip
                      key={option.label}
                      label={option.label}
                      color={colors.accent}
                      selected={selection.options.includes(option.label)}
                      onPress={() =>
                        update(question.question, {
                          options: toggle(selection.options, option.label, question.multiSelect),
                        })
                      }
                    />
                  ))}
                </View>
                {question.options
                  .filter((option) => option.description && selection.options.includes(option.label))
                  .map((option) => (
                    <Text key={`${option.label}-desc`} style={styles.optionDescription}>
                      {option.label}: {option.description}
                    </Text>
                  ))}
                <TextInput
                  value={selection.customText}
                  onChangeText={(value) => update(question.question, { customText: value })}
                  placeholder="직접 입력 (선택)"
                  placeholderTextColor={colors.textFaint}
                  style={styles.customInput}
                  multiline
                />
              </View>
            );
          })}
          <Button label="답변" tone="primary" busy={busy} disabled={!hasAnswer} onPress={submitAnswers} />
        </View>
      ) : (
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
      )}
    </Card>
  );
}

const styles = StyleSheet.create({
  card: { borderColor: colors.accent, gap: spacing.sm, marginVertical: spacing.sm },
  tool: { color: colors.accent, fontSize: 12, fontWeight: '700' },
  title: { color: colors.text, fontSize: 16, fontWeight: '700' },
  headline: { color: colors.textMuted, fontSize: 13 },
  field: { gap: 2 },
  fieldLabel: { color: colors.textFaint, fontSize: 12 },
  fieldValue: { ...monoText, color: colors.text },
  summary: { color: colors.textMuted, fontSize: 13 },
  questions: { gap: spacing.md },
  question: { gap: spacing.xs },
  questionHeader: { color: colors.accent, fontSize: 12, fontWeight: '700' },
  questionText: { color: colors.text, fontSize: 14 },
  multiHint: { color: colors.textFaint, fontSize: 11 },
  options: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, marginTop: spacing.xs },
  optionDescription: { color: colors.textFaint, fontSize: 12 },
  customInput: {
    backgroundColor: colors.surfaceRaised,
    borderColor: colors.border,
    borderRadius: radius.sm,
    borderWidth: StyleSheet.hairlineWidth,
    color: colors.text,
    fontSize: 14,
    marginTop: spacing.xs,
    minHeight: 40,
    padding: spacing.sm,
  },
  actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.xs },
  action: { flex: 1 },
});
