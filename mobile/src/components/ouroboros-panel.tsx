import { useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import type { MobileOuroboros } from '@/api/types';
import { ActionListSheet } from '@/components/sheets';
import { Button } from '@/components/ui';
import { ouroborosPhaseLabel } from '@/lib/mighty';
import { radius, spacing, useStyles, type Palette } from '@/theme';

/**
 * The composer's Ouroboros chrome: where the flow stands, and the steps the host says
 * come next. The host owns the skill list and the phase — the phone only draws them and
 * posts the chosen skill to `/guided`. Skills the host listed in `takesText` carry what
 * is in the composer; the rest go out bare, which the hint under the buttons says.
 */
export function OuroborosPanel({
  ouroboros,
  hasText,
  busySkill,
  disabled,
  onRun,
}: {
  ouroboros: MobileOuroboros;
  /** Whether the composer has something the text-taking skills would carry. */
  hasText: boolean;
  /** The skill currently in flight, if any. */
  busySkill?: string;
  disabled: boolean;
  onRun: (skill: string) => void;
}) {
  const styles = useStyles(makeStyles);
  const [moreOpen, setMoreOpen] = useState(false);
  const nextSkills = new Set(ouroboros.next.map((action) => action.skill));
  const rest = ouroboros.all.filter((action) => !nextSkills.has(action.skill));
  const takesText = ouroboros.next.filter((action) => ouroboros.takesText.includes(action.skill));
  // Nothing can run until the Mac finishes setting Ouroboros up, so the buttons say so
  // by being unpressable rather than by answering 409 a moment later.
  const locked = disabled || !ouroboros.ready;

  return (
    <View style={styles.panel}>
      <View style={styles.headRow}>
        <Text style={styles.phase}>{ouroborosPhaseLabel(ouroboros.phase)}</Text>
        <Text style={styles.headNote}>Ouroboros</Text>
      </View>

      {ouroboros.ready ? null : (
        <Text style={styles.warning}>
          Mac에서 Ouroboros 준비가 끝나지 않았습니다 (플러그인·uvx). Mac의 실행 창에서
          설치한 뒤 다시 시도하세요.
        </Text>
      )}

      {ouroboros.next.length === 0 ? (
        <Text style={styles.hint}>지금은 고를 단계가 없습니다. 아래에 적어 그대로 보내세요.</Text>
      ) : (
        <View style={styles.actions}>
          {ouroboros.next.map((action, index) => (
            <Button
              key={action.skill}
              label={action.title}
              tone={index === 0 ? 'primary' : 'neutral'}
              compact
              busy={busySkill === action.skill}
              disabled={locked || (busySkill !== undefined && busySkill !== action.skill)}
              accessibilityLabel={action.help ? `${action.title} · ${action.help}` : action.title}
              onPress={() => onRun(action.skill)}
            />
          ))}
          {rest.length > 0 ? (
            <Button
              label="더 보기"
              tone="ghost"
              compact
              disabled={locked}
              onPress={() => setMoreOpen(true)}
            />
          ) : null}
        </View>
      )}

      {takesText.length > 0 ? (
        <Text style={styles.hint}>
          {takesText.map((action) => action.title).join(', ')}
          {hasText ? '은(는) 입력창의 내용을 함께 보냅니다.' : '은(는) 입력창의 내용을 함께 보냅니다 (지금은 비어 있습니다).'}
        </Text>
      ) : null}

      <ActionListSheet
        visible={moreOpen}
        title="Ouroboros 스킬"
        note="호스트가 이 실행 창에서 쓸 수 있다고 알려 준 스킬입니다."
        actions={rest.map((action) => ({
          id: action.skill,
          label: action.title,
          description: action.help,
        }))}
        busy={locked || busySkill !== undefined}
        onSelect={(skill) => {
          setMoreOpen(false);
          onRun(skill);
        }}
        onClose={() => setMoreOpen(false)}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    panel: {
      backgroundColor: palette.surface,
      borderRadius: radius.md,
      gap: spacing.xs,
      marginHorizontal: spacing.lg,
      marginBottom: spacing.xs,
      padding: spacing.sm,
    },
    headRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    phase: { color: palette.accent, flex: 1, fontSize: 13, fontWeight: '700' },
    headNote: { color: palette.textFaint, fontSize: 11 },
    actions: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    hint: { color: palette.textFaint, fontSize: 11 },
    warning: { color: palette.warning, fontSize: 12 },
  });
