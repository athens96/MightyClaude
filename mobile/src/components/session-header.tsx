import { StyleSheet, Text, useWindowDimensions, View } from 'react-native';
import type { MobileSessionDetail, MobileSettings, SettingOption } from '@/api/types';
import { StatusGlyph } from '@/components/status-glyph';
import { StatusLineView } from '@/components/status-line-view';
import { Chip } from '@/components/ui';
import { useNow } from '@/hooks/use-now';
import { attentionOf, contextPercentOf, displayStatus, formatClock, liveElapsed } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { statusWord } from '@/lib/status-glyph';
import { toneOf } from '@/lib/status-tone';
import { styleOptions } from '@/lib/styles';
import {
  agentViewModes,
  optionLabel,
  spacing,
  toneColors,
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
  if (field === 'agentViewMode') return agentViewModes();
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

const settingTitleKeys: Record<SettingField, string> = {
  model: 'composer.label.model',
  permissionMode: 'remote.slashHint.permission',
  effort: 'composer.effort.label',
  agentViewMode: 'phone.session.setting.viewMode',
  mightyStyle: 'phone.session.setting.mightyStyle',
  styleId: 'phone.session.setting.mightyStyle',
};

/** What a setting is called on its chip and as its picker's title. */
export function settingTitle(field: SettingField): string {
  return t(settingTitleKeys[field]);
}

/** `Mighty style: gstack · Found in repository` — the badge follows the name everywhere. */
function chipLabel(field: SettingField, options: SettingOption[], value: string): string {
  const badge = options.find((option) => option.id === value)?.badge;
  return `${settingTitle(field)}: ${optionLabel(options, value)}${badge ? ` · ${badge}` : ''}`;
}

/**
 * The pane's title as the navigation bar's centre (concept A): the title on one line and,
 * under it, one quiet line — the status glyph and word in the status ink, then the
 * elapsed time, context used and cost. No status-colour background. Every figure is one
 * the host sent; a figure that is missing is left out, never guessed. The back button and
 * the "⋯" menu stay the navigation bar's own.
 */
export function SessionTitle({
  detail,
  receivedAt,
}: {
  detail: MobileSessionDetail;
  /** When this detail arrived, so a running pane's clock can move between answers. */
  receivedAt?: number;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { width } = useWindowDimensions();
  const { session, usage } = detail;
  const now = useNow(1000, session.status === 'running' && detail.elapsedSeconds !== undefined);
  const elapsedSeconds = liveElapsed(detail, receivedAt, now);
  const contextPercent = contextPercentOf(usage);
  const status = displayStatus(session);
  const word =
    status === 'waiting' ? t('phone.card.attention', { count: attentionOf(session) }) : statusWord(status);
  const ink = toneColors(palette, toneOf(status)).ink;

  const figures: { shown: string; spoken: string }[] = [];
  if (elapsedSeconds !== undefined) {
    const clock = formatClock(elapsedSeconds);
    figures.push({ shown: clock, spoken: `${t('phone.session.hero.elapsed')} ${clock}` });
  }
  if (contextPercent !== undefined) {
    const percent = Math.round(contextPercent);
    figures.push({
      shown: t('phone.card.context', { percent }),
      spoken: t('phone.card.contextLabel', { percent }),
    });
  }
  if (usage?.costUSD !== undefined && Number.isFinite(usage.costUSD)) {
    const cost = `$${usage.costUSD.toFixed(2)}`;
    figures.push({ shown: cost, spoken: `${t('phone.session.hero.cost')} ${cost}` });
  }

  // The bar's own buttons take about 70 points a side; the title gives way before them.
  return (
    <View style={[styles.title, { maxWidth: Math.max(160, width - 140) }]}>
      <Text accessibilityRole="header" numberOfLines={1} style={styles.titleText}>
        {session.title || t('phone.card.untitled')}
      </Text>
      <View
        accessible
        accessibilityLabel={[word, ...figures.map((figure) => figure.spoken)].join(', ')}
        style={styles.subtitle}
      >
        <StatusGlyph status={status} kind={session.kind} size={12} decorative />
        <Text numberOfLines={1} style={styles.subtitleText}>
          <Text style={[styles.word, { color: ink }]}>{word}</Text>
          {figures.map((figure) => ` · ${figure.shown}`).join('')}
        </Text>
      </View>
    </View>
  );
}

/** The chips a pane shows: a field the host sends no options or no value for is left out. */
export function settingFields(settings: MobileSettings | undefined, styleAware: boolean): SettingField[] {
  if (!settings) return [];
  return (
    [
      'model',
      'permissionMode',
      'effort',
      'agentViewMode',
      styleAware ? 'styleId' : 'mightyStyle',
    ] as const
  ).filter((field) => optionsFor(settings, field).length > 0 && valueFor(settings, field).length > 0);
}

/**
 * The expanded session panel above the composer (`SessionPanel`): the settings chips (on
 * a host that advertised "settings") and the status line (on one that sends it).
 */
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

  const fields = settingFields(settings, styleAware);
  const chips = settings !== undefined && fields.length > 0;
  if (!chips && !showStatus) return null;

  return (
    <View style={styles.header}>
      {chips ? (
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
            <Text style={styles.settingNote}>{t('phone.session.settingsLocked')}</Text>
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
    title: { alignItems: 'center' },
    titleText: { color: palette.text, fontSize: 15.5, fontWeight: '700', lineHeight: 20 },
    subtitle: { alignItems: 'center', flexDirection: 'row', gap: 5, marginTop: 1 },
    subtitleText: { color: palette.textMuted, flexShrink: 1, fontSize: 12, lineHeight: 16 },
    word: { fontWeight: '600' },
    header: { gap: spacing.sm },
    settings: { gap: spacing.xs },
    settingChips: { flexDirection: 'row', flexWrap: 'wrap', gap: spacing.xs },
    settingNote: { color: palette.textFaint, fontSize: 11 },
  });
