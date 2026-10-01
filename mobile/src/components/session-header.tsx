import { StyleSheet, Text, View } from 'react-native';
import type { MobileSessionDetail, MobileSettings, SettingOption } from '@/api/types';
import { StatusLineView } from '@/components/status-line-view';
import { BetaBadge, Chip } from '@/components/ui';
import { useNow } from '@/hooks/use-now';
import { attentionOf, contextPercentOf, displayStatus, formatClock, liveElapsed } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { toneOf } from '@/lib/status-tone';
import { styleOptions } from '@/lib/styles';
import {
  AGENT_VIEW_MODES,
  headingFontFamily,
  kindLabel,
  optionLabel,
  providerIsBeta,
  providerLabel,
  radius,
  spacing,
  statusLabel,
  toneColors,
  typeScale,
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

/** The host may leave an option list out; an absent list simply hides its chip. */
export function optionsFor(settings: MobileSettings | undefined, field: SettingField): SettingOption[] {
  if (!settings) return [];
  if (field === 'agentViewMode') return AGENT_VIEW_MODES;
  const options = settings.options as Partial<MobileSettings['options']> | undefined;
  if (field === 'styleId') return styleOptions(options?.styles);
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

/**
 * The top of a pane, concept D's hero: a card filled in the pane's status colour — the
 * title, the status, what runs it, then a row of figures. Every figure is one the host
 * sent (elapsed, context, cost) or one counted from a transcript the phone holds whole
 * (tool calls); a figure that is missing is left out, never guessed. The settings chips
 * and the status line follow under the hero, as before.
 */
export function SessionHeader({
  detail,
  settings,
  showStatus,
  styleAware,
  tools,
  receivedAt,
  onEditSetting,
}: {
  detail: MobileSessionDetail;
  /** Present only on a host that advertised "settings". */
  settings?: MobileSettings;
  /** "status": the host sends a status line and rate limits. */
  showStatus: boolean;
  /** "style": pick from the host's open style list instead of the fixed three. */
  styleAware: boolean;
  /** Tool calls in the transcript, when the whole transcript is on the phone. */
  tools?: number;
  /** When this detail arrived, so a running pane's clock can move between answers. */
  receivedAt?: number;
  onEditSetting: (field: SettingField) => void;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { session, usage } = detail;
  const now = useNow(1000, session.status === 'running' && detail.elapsedSeconds !== undefined);
  const elapsedSeconds = liveElapsed(detail, receivedAt, now);
  const contextPercent = contextPercentOf(usage);
  const status = displayStatus(session);
  const colors = toneColors(palette, toneOf(status));
  const ink = { color: colors.onFill };
  const model = usage?.model ?? session.model;
  const figures: { key: string; value: string; label: string }[] = [];
  if (elapsedSeconds !== undefined) {
    figures.push({ key: 'elapsed', value: formatClock(elapsedSeconds), label: t('phone.session.hero.elapsed') });
  }
  if (contextPercent !== undefined) {
    figures.push({ key: 'context', value: `${contextPercent.toFixed(0)}%`, label: t('phone.session.hero.context') });
  }
  if (usage?.costUSD !== undefined && Number.isFinite(usage.costUSD)) {
    figures.push({ key: 'cost', value: `$${usage.costUSD.toFixed(2)}`, label: t('phone.session.hero.cost') });
  }
  if (tools !== undefined) {
    figures.push({ key: 'tools', value: String(tools), label: t('phone.session.hero.tools') });
  }

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
      <View style={[styles.hero, { backgroundColor: colors.fill }]}>
        <View style={styles.titleRow}>
          <Text accessibilityRole="header" numberOfLines={2} style={[styles.title, ink]}>
            {session.title || t('phone.card.untitled')}
          </Text>
          <View style={[styles.heroPill, { borderColor: colors.onFill }]}>
            <Text numberOfLines={1} style={[styles.heroPillLabel, ink]}>
              {status === 'waiting'
                ? t('phone.card.attention', { count: attentionOf(session) })
                : statusLabel(status)}
            </Text>
          </View>
        </View>
        <View style={styles.metaRow}>
          <Text style={[styles.meta, ink]}>{kindLabel(session.kind)}</Text>
          <Text style={[styles.meta, ink]}>· {providerLabel(session.provider)}</Text>
          {providerIsBeta(session.provider) ? <BetaBadge /> : null}
          {model ? <Text style={[styles.meta, ink]}>· {model}</Text> : null}
        </View>
        {figures.length > 0 ? (
          <View style={styles.figures}>
            {figures.map((figure) => (
              <View
                key={figure.key}
                accessible
                accessibilityLabel={`${figure.label} ${figure.value}`}
                style={styles.figure}
              >
                <Text style={[styles.figureValue, ink]}>{figure.value}</Text>
                <Text style={[styles.figureLabel, ink]}>{figure.label}</Text>
              </View>
            ))}
          </View>
        ) : null}
      </View>

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
    header: { gap: spacing.sm, marginBottom: spacing.md },
    hero: {
      borderRadius: radius.hero,
      gap: 3,
      paddingHorizontal: spacing.lg,
      paddingVertical: spacing.md + 2,
    },
    titleRow: { alignItems: 'flex-start', flexDirection: 'row', gap: spacing.sm },
    title: { ...typeScale.title, flex: 1 },
    heroPill: {
      borderRadius: radius.round,
      borderWidth: 1.5,
      marginTop: 2,
      paddingHorizontal: 8,
      paddingVertical: 2,
    },
    heroPillLabel: { fontSize: 11.5, fontWeight: '700' },
    metaRow: { alignItems: 'center', flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    meta: { fontSize: 12, fontWeight: '500' },
    figures: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.lg, marginTop: spacing.sm },
    figure: { gap: 0 },
    figureValue: {
      fontFamily: headingFontFamily,
      fontSize: 19,
      fontVariant: ['tabular-nums'],
      fontWeight: '700',
      lineHeight: 23,
    },
    figureLabel: { fontSize: 11, fontWeight: '600' },
    settings: { gap: spacing.xs },
    settingChips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    settingNote: { color: palette.textFaint, fontSize: 11 },
  });
