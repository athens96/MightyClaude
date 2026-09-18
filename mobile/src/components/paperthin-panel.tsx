import { useMemo, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import type { MobilePaperthin, PaperthinSkill } from '@/api/types';
import { InfoSheet } from '@/components/sheets';
import { radius, spacing, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The composer's Paperthin chrome: the 2×2 map picks a domain, the domain lists its
 * skills, and the open casebook — when the host sends one — is shown above them.
 * Tapping a skill sends it with whatever is in the composer; a long press explains what
 * it does. The casebook is display only: opening a file is a Mac thing.
 */
export function PaperthinPanel({
  paperthin,
  busySkill,
  disabled,
  onRun,
}: {
  paperthin: MobilePaperthin;
  busySkill?: string;
  disabled: boolean;
  onRun: (skill: string) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const [chosen, setChosen] = useState<string | undefined>(undefined);
  const [detail, setDetail] = useState<PaperthinSkill | undefined>(undefined);

  // The domain the user picked, or the first one the host listed.
  const domain = useMemo(() => {
    const byId = paperthin.domains.find((entry) => entry.id === chosen);
    return byId ?? paperthin.domains[0];
  }, [chosen, paperthin.domains]);

  if (paperthin.domains.length === 0) {
    return (
      <View style={styles.panel}>
        <Text style={styles.hint}>호스트가 Paperthin 지도를 보내지 않았습니다.</Text>
      </View>
    );
  }

  return (
    <View style={styles.panel}>
      {paperthin.installed ? null : (
        <Text style={styles.warning}>
          Paperthin 스킬이 설치되어 있지 않습니다. 스킬 설치는 Mac에서 합니다.
        </Text>
      )}

      <View style={styles.map}>
        {paperthin.domains.map((entry) => {
          const selected = entry.id === domain?.id;
          return (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${entry.title} · ${entry.axis}`}
              accessibilityState={{ selected }}
              key={entry.id}
              onPress={() => setChosen(entry.id)}
              style={({ pressed }) => [
                styles.cell,
                selected && { borderColor: palette.accent, backgroundColor: palette.accentMuted },
                pressed && styles.pressed,
              ]}
            >
              <Text style={[styles.cellTitle, selected && { color: palette.accent }]}>
                {entry.title}
              </Text>
              <Text style={styles.cellAxis}>{entry.axis}</Text>
            </Pressable>
          );
        })}
      </View>

      {domain?.question ? <Text style={styles.question}>{domain.question}</Text> : null}

      {paperthin.casebook ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.casebook}>
          <View style={styles.casebookRow}>
            <Text style={styles.casebookName}>{paperthin.casebook.name}</Text>
            {paperthin.casebook.weight ? (
              <Text style={styles.casebookWeight}>{paperthin.casebook.weight}</Text>
            ) : null}
            {/* Two case files can share a name under different folders. */}
            {paperthin.casebook.files.map((file, index) => (
              <Text key={index} style={styles.casebookFile}>
                {file}
              </Text>
            ))}
          </View>
        </ScrollView>
      ) : null}

      <View style={styles.skills}>
        {(domain?.skills ?? []).map((skill) => {
          const recommended = skill.name === paperthin.recommended;
          const busy = busySkill === skill.name;
          // Nothing can run until the Mac has the skills installed, so the buttons say
          // so by being unpressable rather than by answering 400 a moment later.
          const locked = !paperthin.installed || disabled || (busySkill !== undefined && !busy);
          return (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={`${skill.name} · ${skill.summary}`}
              accessibilityState={{ disabled: locked, busy }}
              disabled={locked}
              key={skill.name}
              onLongPress={() => setDetail(skill)}
              onPress={() => onRun(skill.name)}
              style={({ pressed }) => [
                styles.skill,
                recommended && { borderColor: palette.accent },
                busy && { borderColor: palette.accent, backgroundColor: palette.accentMuted },
                locked && styles.locked,
                pressed && styles.pressed,
              ]}
            >
              {skill.emoji ? <Text style={styles.emoji}>{skill.emoji}</Text> : null}
              <Text style={[styles.skillName, recommended && { color: palette.accent }]}>
                {skill.name}
              </Text>
              {skill.userInvoked ? <Text style={styles.flag}>👤</Text> : null}
              {skill.readOnly ? <Text style={styles.flag}>👁</Text> : null}
            </Pressable>
          );
        })}
      </View>

      <Text style={styles.hint}>
        대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 길게 누르면 설명이 나옵니다.
      </Text>

      <InfoSheet
        visible={detail !== undefined}
        title={detail ? `${detail.emoji} ${detail.name}`.trim() : ''}
        lines={detail ? skillLines(detail) : []}
        onClose={() => setDetail(undefined)}
      />
    </View>
  );
}

/** The Mac's own tooltip, one line at a time. */
function skillLines(skill: PaperthinSkill): string[] {
  const lines: string[] = [];
  if (skill.summary) lines.push(skill.summary);
  if (skill.scope) lines.push(`범위: ${skill.scope}`);
  lines.push(skill.userInvoked ? '사람만 부를 수 있는 스킬' : '모델도 스스로 꺼내 씀');
  if (skill.readOnly) lines.push('읽기 전용');
  return lines;
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
    map: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    cell: {
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      flexGrow: 1,
      flexBasis: '46%',
      gap: 1,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    cellTitle: { color: palette.text, fontSize: 12, fontWeight: '700' },
    cellAxis: { color: palette.textFaint, fontSize: 10 },
    question: { color: palette.textMuted, fontSize: 12 },
    casebook: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      padding: spacing.xs,
    },
    casebookRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    casebookName: { color: palette.text, fontSize: 12, fontWeight: '600' },
    casebookWeight: { color: palette.textFaint, fontSize: 10 },
    casebookFile: { color: palette.textMuted, fontSize: 11 },
    skills: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    skill: {
      alignItems: 'center',
      borderColor: palette.border,
      borderRadius: radius.sm,
      borderWidth: StyleSheet.hairlineWidth,
      flexDirection: 'row',
      gap: 4,
      minHeight: 30,
      paddingHorizontal: spacing.sm,
      paddingVertical: spacing.xs,
    },
    emoji: { fontSize: 12 },
    skillName: { color: palette.text, fontSize: 12, fontWeight: '600' },
    flag: { color: palette.textFaint, fontSize: 10 },
    hint: { color: palette.textFaint, fontSize: 11 },
    warning: { color: palette.warning, fontSize: 12 },
    locked: { opacity: 0.45 },
    pressed: { opacity: 0.7 },
  });
