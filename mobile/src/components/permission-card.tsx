import { useState } from 'react';
import { Pressable, StyleSheet, Text, TextInput, View } from 'react-native';
import type { MobilePermission, Question, QuestionAnswers } from '@/api/types';
import { Button, Card, Chip } from '@/components/ui';
import {
  canGoBack,
  canGoNext,
  canGoTo,
  createQuestionnaireState,
  currentQuestion,
  goBack,
  goNext,
  goTo,
  isAnswered,
  isComplete,
  isLastQuestion,
  pickFor,
  progressLabel,
  setCustomText,
  toggleOption,
  buildAnswers,
  type QuestionnaireState,
} from '@/lib/questionnaire';
import { monoText, radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

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
  const styles = useStyles(makeStyles);
  // Picks live in this component only: the card is keyed by the request id, so when the
  // host drops the request the card unmounts and every answer typed here is discarded.
  // Nothing is written to disk and nothing survives a reconnect.
  const [state, setState] = useState(createQuestionnaireState);

  const questions = permission.questionnaire?.questions ?? [];
  const question = currentQuestion(questions, state);

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

      {question ? (
        <View style={styles.questions}>
          {questions.length > 1 ? (
            <View style={styles.progressRow}>
              <Text style={styles.progress}>{progressLabel(questions, state)}</Text>
              <ProgressDots
                questions={questions}
                state={state}
                onJump={(index) => setState((prev) => goTo(questions, prev, index))}
              />
            </View>
          ) : null}

          <QuestionView
            question={question}
            selected={pickFor(state, question).selectedOptions}
            customText={pickFor(state, question).customText}
            onToggle={(label) => setState((prev) => toggleOption(prev, question, label))}
            onCustomText={(value) => setState((prev) => setCustomText(prev, question, value))}
          />

          {questions.length > 1 ? (
            <View style={styles.navigation}>
              <Button
                label="이전"
                tone="neutral"
                compact
                style={styles.navButton}
                disabled={!canGoBack(state)}
                onPress={() => setState(goBack)}
              />
              <Button
                label="다음"
                tone="neutral"
                compact
                style={styles.navButton}
                disabled={!canGoNext(questions, state)}
                onPress={() => setState((prev) => goNext(questions, prev))}
              />
            </View>
          ) : null}

          {isLastQuestion(questions, state) ? (
            <Button
              label="답변 보내기"
              tone="primary"
              busy={busy}
              disabled={!isComplete(questions, state)}
              onPress={() => onAnswer(buildAnswers(questions, state))}
            />
          ) : null}
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

/**
 * One dot per question, as on the Mac: filled once the question has an answer, a ring
 * for the one on screen. Tapping walks back freely and forward only over answers that
 * are already there.
 */
function ProgressDots({
  questions,
  state,
  onJump,
}: {
  questions: readonly Question[];
  state: QuestionnaireState;
  onJump: (index: number) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.dots}>
      {questions.map((question, index) => {
        const current = currentQuestion(questions, state) === question;
        const answered = isAnswered(state, question);
        const reachable = canGoTo(questions, state, index);
        return (
          <Pressable
            accessibilityLabel={`질문 ${index + 1}`}
            accessibilityRole="button"
            accessibilityState={{ selected: current, disabled: !reachable }}
            disabled={!reachable}
            hitSlop={6}
            key={question.question}
            onPress={() => onJump(index)}
            style={[
              styles.dot,
              { borderColor: answered || current ? palette.accent : palette.border },
              answered && !current && { backgroundColor: palette.accent },
              !reachable && styles.dotLocked,
            ]}
          />
        );
      })}
    </View>
  );
}

function QuestionView({
  question,
  selected,
  customText,
  onToggle,
  onCustomText,
}: {
  question: Question;
  selected: string[];
  customText: string;
  onToggle: (label: string) => void;
  onCustomText: (value: string) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <View style={styles.question}>
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
            color={palette.accent}
            selected={selected.includes(option.label)}
            onPress={() => onToggle(option.label)}
          />
        ))}
      </View>
      {question.options
        .filter((option) => option.description && selected.includes(option.label))
        .map((option) => (
          <Text key={`${option.label}-desc`} style={styles.optionDescription}>
            {option.label}: {option.description}
          </Text>
        ))}
      <TextInput
        value={customText}
        onChangeText={onCustomText}
        placeholder="직접 입력 (선택)"
        placeholderTextColor={palette.textFaint}
        style={styles.customInput}
        multiline
      />
    </View>
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
    questions: { gap: spacing.md },
    progressRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    progress: { color: palette.textMuted, flex: 1, fontSize: 12, fontWeight: '700' },
    dots: { flexDirection: 'row', gap: spacing.xs },
    dot: { borderRadius: 5, borderWidth: 1.5, height: 10, width: 10 },
    dotLocked: { opacity: 0.4 },
    question: { gap: spacing.xs },
    questionHeader: { color: palette.accent, fontSize: 12, fontWeight: '700' },
    questionText: { color: palette.text, fontSize: 14 },
    multiHint: { color: palette.textFaint, fontSize: 11 },
    options: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.sm, marginTop: spacing.xs },
    optionDescription: { color: palette.textFaint, fontSize: 12 },
    customInput: {
      backgroundColor: palette.surfaceRaised,
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontSize: 14,
      marginTop: spacing.xs,
      minHeight: 40,
      padding: spacing.sm,
    },
    navigation: { flexDirection: 'row', gap: spacing.sm },
    navButton: { flex: 1 },
    actions: { flexDirection: 'row', gap: spacing.sm, marginTop: spacing.xs },
    action: { flex: 1 },
  });
