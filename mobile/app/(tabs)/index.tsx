import { useMemo, useState } from 'react';
import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeRepairNeeded } from '@/api/relay/transport';
import { LiveDot, reachabilityKeys } from '@/components/host-status';
import { HostWorkspaces } from '@/components/host-workspaces';
import { Icon } from '@/components/icons';
import { PickerSheet } from '@/components/sheets';
import { EmptyState, ErrorBanner, ScreenTitle } from '@/components/ui';
import { countStats, resolveSelectedHost } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { refreshHostPoll, useDashboardStore } from '@/store/dashboard';
import { useHostsStore } from '@/store/hosts';
import { useHostState } from '@/store/live';
import {
  cardShadow,
  radius,
  spacing,
  toneColors,
  typeScale,
  useStyles,
  usePalette,
  type Palette,
} from '@/theme';
import type { Tone } from '@/lib/status-tone';

/**
 * A count in a coloured tile. A tile with nothing to count steps back to its soft tint,
 * so the eye goes to the ones that hold something; "완료" is always a white card with a
 * green figure, as in the concept.
 */
function StatTile({
  count,
  label,
  tone,
  quiet = false,
  wide = false,
}: {
  count: number;
  label: string;
  tone: Tone;
  /** Drawn as a white card with the figure in the tone's ink. */
  quiet?: boolean;
  wide?: boolean;
}) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const colors = toneColors(palette, tone);
  const filled = !quiet && count > 0;
  const background = quiet ? palette.surface : filled ? colors.fill : colors.soft;
  const figure = filled ? colors.onFill : colors.ink;
  const word = filled ? colors.onFill : quiet ? palette.textMuted : colors.ink;
  return (
    <View
      accessible
      accessibilityLabel={t('phone.dashboard.statLabel', { label, count })}
      style={[styles.tile, wide && styles.tileWide, { backgroundColor: background }]}
    >
      <Text style={[styles.tileCount, { color: figure }]}>{count}</Text>
      <Text numberOfLines={1} style={[styles.tileLabel, { color: word }]}>
        {label}
      </Text>
    </View>
  );
}

/** "현황": status first — which host, how many panes run, wait and are done, then every pane. */
export default function DashboardTab() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const hosts = useHostsStore((store) => store.hosts);
  const loaded = useHostsStore((store) => store.loaded);
  const selectedId = useDashboardStore((store) => store.selectedHostId);
  const selectHost = useDashboardStore((store) => store.selectHost);
  const hostId = resolveSelectedHost(hosts, selectedId);
  const host = hosts.find((entry) => entry.id === hostId);
  const state = useHostState(hostId);
  const poll = useDashboardStore((store) => (hostId ? store.polls[hostId] : undefined));
  const reachability = useHostsStore((store) =>
    hostId ? (store.status[hostId]?.reachability ?? 'unknown') : 'unknown',
  );
  const [picking, setPicking] = useState(false);
  const stats = useMemo(() => countStats(state), [state]);
  const hostName = state?.hostName || host?.name || '';
  const needsRepair = poll?.needsRepair || reachability === 'unauthorized';

  return (
    <View style={styles.screen}>
      <ScrollView
        contentContainerStyle={[
          styles.content,
          { paddingTop: insets.top + spacing.sm, paddingBottom: spacing.xxl },
        ]}
        refreshControl={
          <RefreshControl
            refreshing={poll?.loading ?? false}
            onRefresh={() => refreshHostPoll(hostId)}
            tintColor={palette.accent}
          />
        }
      >
        <View style={styles.topRow}>
          {host ? (
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t('phone.dashboard.hostChip', {
                name: hostName,
                status: t(reachabilityKeys[reachability]),
              })}
              accessibilityHint={hosts.length > 1 ? t('phone.dashboard.pickHost') : undefined}
              disabled={hosts.length < 2}
              hitSlop={{ bottom: 8, top: 8 }}
              onPress={() => setPicking(true)}
              style={({ pressed }) => [styles.hostChip, pressed && styles.pressed]}
            >
              <LiveDot palette={palette} reachability={reachability} />
              <Text numberOfLines={1} style={styles.hostName}>
                {hostName}
              </Text>
              {hosts.length > 1 ? <Icon name="chevronDown" color={palette.textFaint} size={14} strokeWidth={2.6} /> : null}
            </Pressable>
          ) : (
            <View />
          )}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('phone.hosts.addHost')}
            hitSlop={4}
            onPress={() => router.push('/pair')}
            style={({ pressed }) => [styles.circle, pressed && styles.pressed]}
          >
            <Icon name="plus" color={palette.text} size={20} strokeWidth={2.4} />
          </Pressable>
        </View>

        <ScreenTitle title={t('phone.dashboard.title')} style={styles.title} />

        <View style={styles.stats}>
          <StatTile count={stats.running} label={t('phone.dashboard.stat.running')} tone="run" wide />
          <StatTile count={stats.waiting} label={t('phone.dashboard.stat.waiting')} tone="wait" />
          <StatTile count={stats.done} label={t('phone.dashboard.stat.done')} tone="done" quiet />
        </View>

        {needsRepair ? (
          <ErrorBanner message={describeRepairNeeded(poll?.error)} />
        ) : poll?.error ? (
          <ErrorBanner message={poll.error} />
        ) : null}

        {hostId ? (
          <HostWorkspaces
            hostId={hostId}
            state={state}
            loading={poll?.loading ?? true}
            glance
            onCreated={() => refreshHostPoll(hostId)}
          />
        ) : loaded ? (
          <EmptyState
            title={t('phone.hosts.empty.title')}
            description={t('phone.hosts.empty.description')}
          />
        ) : null}
      </ScrollView>

      <PickerSheet
        visible={picking}
        title={t('phone.dashboard.pickHost')}
        options={hosts.map((entry) => ({ id: entry.id, label: entry.name }))}
        selectedId={hostId}
        busy={false}
        locked={false}
        onSelect={(id) => {
          selectHost(id);
          setPicking(false);
        }}
        onClose={() => setPicking(false)}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    content: { gap: spacing.md, paddingHorizontal: spacing.lg },
    topRow: { alignItems: 'center', flexDirection: 'row', justifyContent: 'space-between' },
    hostChip: {
      ...cardShadow,
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.round,
      flexDirection: 'row',
      flexShrink: 1,
      gap: spacing.md,
      paddingLeft: spacing.md,
      paddingRight: spacing.lg,
      paddingVertical: spacing.sm,
    },
    hostName: { color: palette.text, flexShrink: 1, fontSize: 13, fontWeight: '700' },
    circle: {
      ...cardShadow,
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: radius.round,
      height: 36,
      justifyContent: 'center',
      width: 36,
    },
    pressed: { opacity: 0.75 },
    title: { paddingHorizontal: spacing.xs },
    stats: { flexDirection: 'row', gap: spacing.sm, marginBottom: spacing.xs },
    tile: {
      borderRadius: 18,
      flex: 1,
      paddingHorizontal: spacing.md,
      paddingVertical: spacing.lg,
    },
    tileWide: { flex: 1.25 },
    tileCount: { ...typeScale.number },
    tileLabel: { fontSize: 12, fontWeight: '700' },
  });
