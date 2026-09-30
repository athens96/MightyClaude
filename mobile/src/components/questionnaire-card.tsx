import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import type { MobilePermission, Question, QuestionAnswers, Questionnaire } from '@/api/types';
import { Button } from '@/components/ui';
import { t } from '@/lib/i18n';
import {
  buildAnswers,
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
  selectionHint,
  setCustomText,
  toggleCustom,
  toggleOption,
  type QuestionnaireState,
} from '@/lib/questionnaire';
import { radius, spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The phone's copy of the Mac's `UserQuestionnaireCard`: one AskUserQuestion at a time,
 * docked right above the composer. A header chip and the "하나 선택 / 여러 개 선택 가능"
 * hint, the question, one full-width row per option with its description always shown,
 * and "직접 입력" as the last row, which reveals a text box once chosen. 다음 moves on
 * once the question on screen is answered, 이전 goes back with the picks intact, and only
 * 답변 보내기 on the last question sends anything; 취소 turns the request down.
 *
 * Picks live in this component only: the screen keys it by run and request id, so when the
 * host drops the request the card unmounts and every answer typed here is discarded.
 * Nothing is written to disk and nothing survives a reconnect.
 */
export function QuestionnaireCard({
  permission,
  questionnaire,
  busy,
  waiting,
  maxBodyHeight,
  onCancel,
  onAnswer,
}: {
  permission: MobilePermission;
  questionnaire: Questionnaire;
  busy: boolean;
  /** How many question requests the pane holds, this one included. */
  waiting: number;
  /** The ceiling of the scrolling question body, so the card never walks the transcript off screen. */
  maxBodyHeight: number;
  onCancel: () => void;
  onAnswer: (answers: QuestionAnswers) => void;
}) {
  const styles = useStyles(makeStyles);
  const [state, setState] = useState(createQuestionnaireState);
  const body = useRef<ScrollView | null>(null);
  const customInput = useRef<TextInput | null>(null);
  const scrollFrame = useRef<number | null>(null);

  /**
   * The text box is the last thing in the body. While it has focus it is brought back into
   * view whenever the body changes size — the keyboard coming up shrinks it, a new line
   * grows the box — rather than left underneath. One frame later, once the layout settles.
   */
  const revealCustom = useCallback(() => {
    if (scrollFrame.current !== null) cancelAnimationFrame(scrollFrame.current);
    scrollFrame.current = requestAnimationFrame(() => {
      scrollFrame.current = null;
      body.current?.scrollToEnd({ animated: true });
    });
  }, []);
  const revealFocusedCustom = useCallback(() => {
    if (customInput.current?.isFocused()) revealCustom();
  }, [revealCustom]);

  // The keyboard coming up lowers the ceiling of the body.
  useEffect(() => {
    revealFocusedCustom();
  }, [maxBodyHeight, revealFocusedCustom]);
  useEffect(
    () => () => {
      if (scrollFrame.current !== null) cancelAnimationFrame(scrollFrame.current);
    },
    [],
  );

  const questions = questionnaire.questions;
  const question = currentQuestion(questions, state);
  const last = isLastQuestion(questions, state);

  return (
    <View accessibilityLabel={t('phone.questionnaire.title')} style={styles.card} testID={`questionnaire-${permission.id}`}>
      <View style={styles.headRow}>
        <Text style={styles.title}>{t('phone.questionnaire.title')}</Text>
        {questions.length > 1 ? (
          <>
            <Text style={styles.progress}>{progressLabel(questions, state)}</Text>
            <ProgressDots
              questions={questions}
              state={state}
              disabled={busy}
              onJump={(index) => setState((prev) => goTo(questions, prev, index))}
            />
          </>
        ) : null}
        <View style={styles.spacer} />
        {waiting > 1 ? <Text style={styles.waiting}>{t('phone.questionnaire.waiting', { count: waiting })}</Text> : null}
      </View>

      <ScrollView
        ref={body}
        keyboardShouldPersistTaps="handled"
        style={{ maxHeight: maxBodyHeight }}
        contentContainerStyle={styles.body}
        onContentSizeChange={revealFocusedCustom}
        onLayout={revealFocusedCustom}
      >
        {question ? (
          <QuestionView
            key={question.question}
            question={question}
            state={state}
            disabled={busy}
            onToggle={(label) => setState((prev) => toggleOption(prev, question, label))}
            onToggleCustom={() => setState((prev) => toggleCustom(prev, question))}
            onCustomText={(value) => setState((prev) => setCustomText(prev, question, value))}
            customInputRef={customInput}
            onCustomFocus={revealCustom}
          />
        ) : null}
      </ScrollView>

      <Text style={styles.hint}>
        {last ? t('phone.questionnaire.hintSubmit') : t('phone.questionnaire.hintNext')}
      </Text>
      <View style={styles.actions}>
        <Button
          label={t('phone.questionnaire.cancel')}
          tone="neutral"
          compact
          disabled={busy}
          style={styles.action}
          onPress={onCancel}
        />
        {canGoBack(state) ? (
          <Button
            label={t('phone.questionnaire.back')}
            tone="neutral"
            compact
            disabled={busy}
            style={styles.action}
            onPress={() => setState(goBack)}
          />
        ) : null}
        {last ? (
          <Button
            label={t('phone.questionnaire.submit')}
            tone="primary"
            compact
            busy={busy}
            disabled={!isComplete(questions, state)}
            style={styles.action}
            onPress={() => onAnswer(buildAnswers(questions, state))}
          />
        ) : (
          <Button
            label={t('phone.questionnaire.next')}
            tone="primary"
            compact
            disabled={busy || !canGoNext(questions, state)}
            style={styles.action}
            onPress={() => setState((prev) => goNext(questions, prev))}
          />
        )}
      </View>
    </View>
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
  disabled,
  onJump,
}: {
  questions: readonly Question[];
  state: QuestionnaireState;
  disabled: boolean;
  onJump: (index: number) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const shown = currentQuestion(questions, state);
  return (
    <View style={styles.dots}>
      {questions.map((question, index) => {
        const current = shown === question;
        const answered = isAnswered(state, question);
        const reachable = !disabled && canGoTo(questions, state, index);
        return (
          <Pressable
            accessibilityLabel={t('phone.questionnaire.jump', { index: index + 1 })}
            accessibilityRole="button"
            accessibilityState={{ selected: current, disabled: !reachable }}
            disabled={!reachable}
            hitSlop={6}
            key={question.question}
            onPress={() => onJump(index)}
            style={[
              styles.dot,
              { borderColor: answered || current ? palette.accent : palette.border },
              answered && { backgroundColor: palette.accent },
              current && styles.dotCurrent,
              !reachable && !current && styles.dotLocked,
            ]}
          />
        );
      })}
    </View>
  );
}

function QuestionView({
  question,
  state,
  disabled,
  onToggle,
  onToggleCustom,
  onCustomText,
  customInputRef,
  onCustomFocus,
}: {
  question: Question;
  state: QuestionnaireState;
  disabled: boolean;
  onToggle: (label: string) => void;
  onToggleCustom: () => void;
  onCustomText: (value: string) => void;
  customInputRef: RefObject<TextInput | null>;
  onCustomFocus: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const pick = pickFor(state, question);
  return (
    <View style={styles.question}>
      <View style={styles.questionHead}>
        <Text numberOfLines={1} style={styles.headerChip}>
          {question.header}
        </Text>
        <View style={styles.spacer} />
        <Text style={styles.selectionHint}>{selectionHint(question)}</Text>
      </View>
      <Text selectable style={styles.questionText}>
        {question.question}
      </Text>

      {question.options.map((option) => (
        <ChoiceRow
          key={option.label}
          label={option.label}
          description={option.description}
          multiple={question.multiSelect}
          selected={pick.selectedOptions.includes(option.label)}
          disabled={disabled}
          onPress={() => onToggle(option.label)}
        />
      ))}
      <ChoiceRow
        label={t('phone.questionnaire.custom')}
        hint={t('phone.questionnaire.customHint')}
        multiple={question.multiSelect}
        selected={pick.customChosen}
        disabled={disabled}
        onPress={onToggleCustom}
      />
      {pick.customChosen ? (
        <TextInput
          accessibilityLabel={t('phone.questionnaire.customLabel', { header: question.header })}
          editable={!disabled}
          multiline
          onChangeText={onCustomText}
          onFocus={onCustomFocus}
          placeholder={t('phone.questionnaire.customPlaceholder')}
          placeholderTextColor={palette.textFaint}
          ref={customInputRef}
          style={styles.customInput}
          value={pick.customText}
        />
      ) : null}
    </View>
  );
}

/** A full-width row: a radio or checkbox, the label, and its description always under it. */
function ChoiceRow({
  label,
  description,
  hint,
  multiple,
  selected,
  disabled,
  onPress,
}: {
  label: string;
  description?: string;
  hint?: string;
  multiple: boolean;
  selected: boolean;
  disabled: boolean;
  onPress: () => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  return (
    <Pressable
      accessibilityHint={description || hint}
      accessibilityLabel={label}
      accessibilityRole={multiple ? 'checkbox' : 'radio'}
      accessibilityState={{ checked: selected, selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.choice,
        selected && styles.choiceSelected,
        pressed && styles.choicePressed,
        disabled && styles.choiceDisabled,
      ]}
    >
      <View
        style={[
          multiple ? styles.checkbox : styles.radio,
          { borderColor: selected ? palette.accent : palette.textFaint },
          multiple && selected && { backgroundColor: palette.accent },
        ]}
      >
        {selected ? (
          multiple ? <Text style={styles.checkMark}>✓</Text> : <View style={styles.radioDot} />
        ) : null}
      </View>
      <View style={styles.choiceText}>
        <Text style={[styles.choiceLabel, selected && styles.choiceLabelSelected]}>{label}</Text>
        {description ? <Text style={styles.choiceDescription}>{description}</Text> : null}
      </View>
    </Pressable>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    // Docked above the composer as a sheet of lighter paper; the accent is left to the
    // progress dots and the chosen answer.
    card: {
      backgroundColor: palette.surface,
      borderColor: palette.border,
      borderRadius: radius.lg,
      borderWidth: StyleSheet.hairlineWidth,
      gap: spacing.sm,
      marginBottom: spacing.xs,
      marginHorizontal: spacing.lg,
      padding: spacing.md,
    },
    headRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    title: { ...typeScale.heading, color: palette.text, fontSize: 16, lineHeight: 22 },
    progress: { color: palette.textMuted, fontSize: 12, fontVariant: ['tabular-nums'] },
    spacer: { flex: 1 },
    waiting: { color: palette.textMuted, fontSize: 12 },
    dots: { alignItems: 'center', flexDirection: 'row', gap: spacing.xs },
    dot: { borderRadius: radius.round, borderWidth: 1.5, height: 8, width: 8 },
    dotCurrent: { height: 10, width: 10 },
    dotLocked: { opacity: 0.4 },
    body: { paddingBottom: 2 },
    question: { gap: spacing.sm },
    questionHead: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    headerChip: {
      color: palette.textMuted,
      flexShrink: 1,
      fontSize: 11,
      fontWeight: '600',
      letterSpacing: 0.2,
      overflow: 'hidden',
      paddingVertical: 2,
    },
    selectionHint: { color: palette.textFaint, fontSize: 11 },
    questionText: { color: palette.text, fontSize: 15, fontWeight: '500', lineHeight: 21 },
    choice: {
      alignItems: 'flex-start',
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: spacing.sm,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.sm,
    },
    choiceSelected: { backgroundColor: palette.accentMuted, borderColor: palette.accent },
    choicePressed: { opacity: 0.7 },
    choiceDisabled: { opacity: 0.5 },
    radio: {
      alignItems: 'center',
      borderRadius: radius.round,
      borderWidth: 1.5,
      height: 18,
      justifyContent: 'center',
      marginTop: 1,
      width: 18,
    },
    radioDot: { backgroundColor: palette.accent, borderRadius: radius.round, height: 10, width: 10 },
    checkbox: {
      alignItems: 'center',
      borderRadius: radius.sm,
      borderWidth: 1.5,
      height: 18,
      justifyContent: 'center',
      marginTop: 1,
      width: 18,
    },
    checkMark: { color: palette.onAccent, fontSize: 12, fontWeight: '800', lineHeight: 14 },
    choiceText: { flex: 1, gap: 2 },
    choiceLabel: { color: palette.text, fontSize: 14, fontWeight: '500' },
    choiceLabelSelected: { fontWeight: '600' },
    choiceDescription: { color: palette.textMuted, fontSize: 12, lineHeight: 17 },
    customInput: {
      backgroundColor: palette.background,
      borderColor: palette.border,
      borderRadius: radius.md,
      borderWidth: StyleSheet.hairlineWidth,
      color: palette.text,
      fontSize: 14,
      maxHeight: 96,
      minHeight: 40,
      padding: spacing.sm,
    },
    hint: { color: palette.textFaint, fontSize: 11 },
    actions: { flexDirection: 'row', gap: spacing.sm },
    action: { flex: 1 },
  });
