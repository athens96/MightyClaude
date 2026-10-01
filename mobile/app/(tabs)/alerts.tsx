import { useMemo } from 'react';
import { Pressable, SectionList, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { openSession } from '@/components/host-workspaces';
import { PaneAvatar, SessionPill, ageText } from '@/components/session-card';
import { EmptyState, ScreenTitle } from '@/components/ui';
import { useNow } from '@/hooks/use-now';
import { buildAttentionList, sessionTone, type AttentionItem, type AttentionReason } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { useHostsStore } from '@/store/hosts';
import { useLiveStore } from '@/store/live';
import { radius, spacing, toneColors, useStyles, usePalette, type Palette } from '@/theme';

const REASON_KEYS: Record<AttentionReason, string> = {
  question: 'phone.alerts.reason.question',
  permission: 'phone.alerts.reason.permission',
  error: 'phone.alerts.reason.error',
  finished: 'phone.alerts.reason.finished',
};

function AlertRow({ item }: { item: AttentionItem }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const colors = toneColors(palette, sessionTone(item.session));
  const title = item.session.title || t('phone.card.untitled');
  const now = useNow(60_000);
  const age = ageText(item.session.updatedAt, now);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t('phone.alerts.rowLabel', { title, reason: t(REASON_KEYS[item.reason]), host: item.hostName })}
      onPress={() => openSession(item.hostId, item.session)}
      style={({ pressed }) => [styles.row, pressed && styles.pressed]}
    >
      <View style={[styles.edge, { backgroundColor: colors.fill }]} />
      <PaneAvatar session={item.session} size={30} />
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
      <SessionPill session={item.session} />
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
      renderItem={({ item }) => <AlertRow item={item} />}
      renderSectionHeader={({ section }) => (
        <Text accessibilityRole="header" style={styles.sectionTitle}>
          {section.title}
        </Text>
      )}
      ItemSeparatorComponent={() => <View style={styles.separator} />}
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
    content: { paddingHorizontal: spacing.md + 2 },
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
    separator: { height: spacing.sm },
    row: {
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.card,
      flexDirection: 'row',
      gap: spacing.sm + 2,
      overflow: 'hidden',
      paddingLeft: spacing.lg + 2,
      paddingRight: spacing.md,
      paddingVertical: spacing.md,
    },
    pressed: { opacity: 0.85, transform: [{ scale: 0.99 }] },
    edge: { bottom: 0, left: 0, position: 'absolute', top: 0, width: 5 },
    body: { flex: 1, gap: 1 },
    rowTitle: { color: palette.text, fontSize: 15, fontWeight: '700' },
    reason: { fontSize: 12.5, fontWeight: '700' },
    meta: { color: palette.textMuted, fontSize: 12 },
  });
