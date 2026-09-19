import { StyleSheet, Text, View } from 'react-native';
import type { MobileSessionDetail, MobileSettings, SettingOption } from '@/api/types';
import { ProviderTag } from '@/components/provider-mark';
import { StatusChip } from '@/components/status-chip';
import { StatusLineView } from '@/components/status-line-view';
import { Chip } from '@/components/ui';
import { sourceBadge } from '@/lib/styles';
import {
  AGENT_VIEW_MODES,
  kindLabel,
  optionLabel,
  radius,
  spacing,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';

/** Which picker a settings chip opens. */
export type SettingField =
  | 'model'
  | 'permissionMode'
  | 'effort'
  | 'agentViewMode'
  | 'mightyStyle'
  /** The open style list a host with "style" sends, in place of `mightyStyle`. */
  | 'styleId';

/** The wire word for a pane running no guided style at all. */
const CLI_STYLE = 'cli';

function formatElapsed(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  const minutes = Math.floor(total / 60);
  const rest = total % 60;
  if (minutes === 0) return `${rest}초`;
  return `${minutes}분 ${rest}초`;
}

/** The host may leave an option list out; an absent list simply hides its chip. */
export function optionsFor(settings: MobileSettings | undefined, field: SettingField): SettingOption[] {
  if (!settings) return [];
  if (field === 'agentViewMode') return AGENT_VIEW_MODES;
  const options = settings.options as Partial<MobileSettings['options']> | undefined;
  if (field === 'styleId') {
    const styles = options?.styles;
    if (!Array.isArray(styles)) return [];
    // A style whose name is not one the app shipped is named with its source wherever
    // the name appears, the picker included (contract 1.10). `cli` is the one entry
    // that is no style at all, so it arrives without a source and wears no badge.
    return styles.map((style) => {
      const option: SettingOption = { id: style.id, label: style.label || style.id };
      if (style.id === CLI_STYLE && style.source === undefined) return option;
      const badge = sourceBadge(style.source);
      if (badge) option.badge = badge;
      return option;
    });
  }
  const list =
    field === 'model'
      ? options?.models
      : field === 'permissionMode'
        ? options?.permissionModes
        : field === 'effort'
          ? options?.efforts
          : options?.mightyStyles;
  return Array.isArray(list) ? list : [];
}

/** The value the host currently reports for a field, or '' when it sends none. */
export function valueFor(settings: MobileSettings, field: SettingField): string {
  switch (field) {
    case 'model':
      return settings.model ?? '';
    case 'permissionMode':
      return settings.permissionMode ?? '';
    case 'effort':
      return settings.effort ?? '';
    case 'agentViewMode':
      return settings.agentViewMode ?? '';
    case 'mightyStyle':
      return settings.mightyStyle ?? '';
    case 'styleId':
      return settings.styleId ?? '';
  }
}

export const settingTitles: Record<SettingField, string> = {
  model: '모델',
  permissionMode: '권한 모드',
  effort: '사고 강도',
  agentViewMode: '보기 방식',
  mightyStyle: 'Mighty 스타일',
  styleId: 'Mighty 스타일',
};

/** `Mighty 스타일: gstack · 저장소에서 발견됨` — the badge follows the name everywhere. */
function chipLabel(field: SettingField, options: SettingOption[], value: string): string {
  const badge = options.find((option) => option.id === value)?.badge;
  return `${settingTitles[field]}: ${optionLabel(options, value)}${badge ? ` · ${badge}` : ''}`;
}

export function SessionHeader({
  detail,
  settings,
  showStatus,
  styleAware,
  onEditSetting,
}: {
  detail: MobileSessionDetail;
  /** Present only on a host that advertised "settings". */
  settings?: MobileSettings;
  /** "status": the host sends a status line and rate limits. */
  showStatus: boolean;
  /** "style": pick from the host's open style list instead of the fixed three. */
  styleAware: boolean;
  onEditSetting: (field: SettingField) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { session, usage, elapsedSeconds } = detail;
  const contextPercent =
    usage?.contextPercent ??
    (usage?.contextUsedTokens !== undefined && usage.contextWindowTokens
      ? (usage.contextUsedTokens / usage.contextWindowTokens) * 100
      : undefined);

  const fields: SettingField[] = settings
    ? (
        [
          'model',
          'permissionMode',
          'effort',
          'agentViewMode',
          styleAware ? 'styleId' : 'mightyStyle',
        ] as const
      ).filter(
        (field) => optionsFor(settings, field).length > 0 && valueFor(settings, field).length > 0,
      )
    : [];

  return (
    <View style={styles.header}>
      <View style={styles.titleRow}>
        <Text numberOfLines={2} style={styles.title}>
          {session.title || '제목 없음'}
        </Text>
        <StatusChip status={session.status} />
      </View>
      <View style={styles.metaRow}>
        <Text style={styles.meta}>{kindLabel(session.kind)}</Text>
        <ProviderTag provider={session.provider} />
        {(usage?.model ?? session.model) ? (
          <Text style={styles.meta}>· {usage?.model ?? session.model}</Text>
        ) : null}
        {elapsedSeconds !== undefined ? (
          <Text style={styles.meta}>· {formatElapsed(elapsedSeconds)}</Text>
        ) : null}
        {contextPercent !== undefined ? (
          <Text style={styles.meta}>· 컨텍스트 {contextPercent.toFixed(0)}%</Text>
        ) : null}
        {usage?.costUSD !== undefined ? (
          <Text style={styles.meta}>· ${usage.costUSD.toFixed(2)}</Text>
        ) : null}
      </View>

      {contextPercent !== undefined ? (
        <View style={styles.track}>
          <View
            style={[styles.fill, { width: `${Math.min(100, Math.max(0, contextPercent))}%` }]}
          />
        </View>
      ) : null}

      {settings && fields.length > 0 ? (
        <View style={styles.settings}>
          <View style={styles.settingChips}>
            {fields.map((field) => (
              <Chip
                key={field}
                label={chipLabel(field, optionsFor(settings, field), valueFor(settings, field))}
                color={palette.textMuted}
                disabled={!settings.editable}
                onPress={() => onEditSetting(field)}
              />
            ))}
          </View>
          {settings.editable ? null : (
            <Text style={styles.settingNote}>실행 중에는 설정을 바꿀 수 없습니다.</Text>
          )}
        </View>
      ) : null}

      {showStatus ? (
        <StatusLineView statusLine={detail.statusLine} rateLimits={detail.rateLimits} />
      ) : null}
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    header: {
      borderBottomColor: palette.border,
      borderBottomWidth: StyleSheet.hairlineWidth,
      gap: spacing.xs,
      paddingBottom: spacing.md,
      marginBottom: spacing.sm,
    },
    titleRow: { alignItems: 'flex-start', flexDirection: 'row', gap: spacing.sm },
    title: { color: palette.text, flex: 1, fontSize: 16, fontWeight: '700' },
    metaRow: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    meta: { color: palette.textFaint, fontSize: 12 },
    track: {
      backgroundColor: palette.surfaceRaised,
      borderRadius: radius.sm,
      height: 4,
      marginTop: spacing.xs,
      overflow: 'hidden',
    },
    fill: { backgroundColor: palette.accent, height: 4 },
    settings: { gap: spacing.xs, marginTop: spacing.xs },
    settingChips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    settingNote: { color: palette.textFaint, fontSize: 11 },
  });
