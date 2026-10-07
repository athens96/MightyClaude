import { useMemo } from 'react';
import { Pressable, SectionList, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { openSession } from '@/components/host-workspaces';
import { ROW_GAP, ROW_GLYPH, ROW_PADDING, ROW_TEXT_INSET, ageText } from '@/components/session-card';
import { StatusGlyph } from '@/components/status-glyph';
import { EmptyState, ScreenTitle } from '@/components/ui';
import { useNow } from '@/hooks/use-now';
import {
  buildAttentionList,
  displayStatus,
  sessionTone,
  type AttentionItem,
  type AttentionReason,
} from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { waitLabel } from '@/lib/session-row';
import { useHostsStore } from '@/store/hosts';
import { useLiveStore } from '@/store/live';
import { spacing, toneColors, useStyles, usePalette, type Palette } from '@/theme';

const REASON_KEYS: Record<AttentionReason, string> = {
  question: 'phone.alerts.reason.question',
  permission: 'phone.alerts.reason.permission',
  error: 'phone.alerts.reason.error',
  finished: 'phone.alerts.reason.finished',
};

/**
 * One pane in a section's list, drawn as the session rows are (concept A): the status
 * glyph, the title, why it is here in its status ink, the host and age, and the amber
 * `질문 1` while it waits on the user. The section's rows read as one rounded list.
 */
function AlertRow({ item, first, last }: { item: AttentionItem; first: boolean; last: boolean }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const colors = toneColors(palette, sessionTone(item.session));
  const title = item.session.title || t('phone.card.untitled');
  const now = useNow(60_000);
  const age = ageText(item.session.updatedAt, now);
  const ask = waitLabel(item.session);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t('phone.alerts.rowLabel', { title, reason: t(REASON_KEYS[item.reason]), host: item.hostName })}
      onPress={() => openSession(item.hostId, item.session)}
      style={({ pressed }) => [
        styles.row,
        first && styles.first,
        last && styles.last,
        pressed && styles.pressed,
      ]}
    >
      {first ? null : <View style={styles.rule} />}
      <View style={styles.glyph}>
        <StatusGlyph status={displayStatus(item.session)} kind={item.session.kind} size={ROW_GLYPH} decorative />
      </View>
      <View style={styles.body}>
        <Text numberOfLines={1} style={styles.rowTitle}>
          {title}
        </Text>
        <Text numberOfLines={1} style={[styles.reason, { color: colors.ink }]}>
          {t(REASON_KEYS[item.reason])}
        </Text>
        <Text numberOfLines={1} style={styles.meta}>
          {age ? `${item.hostName} · ${age}` : item.hostName}
        </Text>
      </View>
      {ask ? <Text style={styles.ask}>{ask}</Text> : null}
    </Pressable>
  );
}

/**
 * "알림": panes waiting on the user (questions, permission requests) and panes with an
 * outcome to look at (errors, finished runs), across every paired host. Built only from
 * the pane lists the phone already holds; tapping a row opens the pane.
 */
export default function AlertsTab() {
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const hosts = useHostsStore((store) => store.hosts);
  const states = useLiveStore((store) => store.states);
  const items = useMemo(() => buildAttentionList(hosts, states), [hosts, states]);

  const sections = useMemo(() => {
    const action = items.filter((item) => item.reason === 'question' || item.reason === 'permission');
    const outcome = items.filter((item) => item.reason === 'error' || item.reason === 'finished');
    return [
      { key: 'action', title: t('phone.alerts.section.action'), data: action },
      { key: 'outcome', title: t('phone.alerts.section.outcome'), data: outcome },
    ].filter((section) => section.data.length > 0);
  }, [items]);

  return (
    <SectionList
      style={styles.screen}
      contentContainerStyle={[
        styles.content,
        { paddingTop: insets.top + spacing.md, paddingBottom: spacing.xxl },
      ]}
      sections={sections}
      keyExtractor={(item) => item.key}
      renderItem={({ item, index, section }) => (
        <AlertRow item={item} first={index === 0} last={index === section.data.length - 1} />
      )}
      renderSectionHeader={({ section }) => (
        <Text accessibilityRole="header" style={styles.sectionTitle}>
          {section.title}
        </Text>
      )}
      stickySectionHeadersEnabled={false}
      ListHeaderComponent={<ScreenTitle title={t('phone.tabs.alerts')} style={styles.title} />}
      ListEmptyComponent={
        <EmptyState
          title={t('phone.alerts.empty.title')}
          description={t('phone.alerts.empty.description')}
        />
      }
    />
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    content: { paddingHorizontal: spacing.lg },
    title: { marginBottom: spacing.sm, paddingHorizontal: spacing.xs },
    sectionTitle: {
      color: palette.textFaint,
      fontSize: 12,
      fontWeight: '700',
      letterSpacing: 0.3,
      paddingBottom: spacing.sm,
      paddingHorizontal: spacing.xs,
      paddingTop: spacing.md,
    },
    row: {
      alignItems: 'flex-start',
      backgroundColor: palette.surface,
      flexDirection: 'row',
      gap: ROW_GAP,
      paddingHorizontal: ROW_PADDING,
      paddingVertical: spacing.md,
    },
    first: { borderTopLeftRadius: 18, borderTopRightRadius: 18 },
    last: { borderBottomLeftRadius: 18, borderBottomRightRadius: 18 },
    pressed: { backgroundColor: palette.surfaceRaised },
    rule: {
      backgroundColor: palette.border,
      height: 1,
      left: ROW_TEXT_INSET,
      position: 'absolute',
      right: 0,
      top: 0,
    },
    glyph: { marginTop: 1 },
    body: { flex: 1, gap: 1, minWidth: 0 },
    rowTitle: { color: palette.text, fontSize: 15, fontWeight: '600', lineHeight: 20 },
    reason: { fontSize: 12.5, fontWeight: '600', lineHeight: 17 },
    meta: { color: palette.textMuted, fontSize: 12.5, lineHeight: 17 },
    ask: { color: palette.warning, fontSize: 12.5, fontWeight: '700', lineHeight: 20 },
  });
