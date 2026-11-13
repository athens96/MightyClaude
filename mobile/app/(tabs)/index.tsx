import { useMemo } from 'react';
import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeRepairNeeded } from '@/api/relay/transport';
import { LiveDot, reachabilityKeys } from '@/components/host-status';
import { HostWorkspaces } from '@/components/host-workspaces';
import { Icon } from '@/components/icons';
import { EmptyState, ErrorBanner, ScreenTitle } from '@/components/ui';
import { countStats, sumStats } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { refreshHostPoll, useDashboardStore } from '@/store/dashboard';
import { useHostsStore, type PairedHost } from '@/store/hosts';
import { useHostState, useLiveStore } from '@/store/live';
import {
  cardShadow,
  headingFontFamily,
  layout,
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

/**
 * One paired computer in "현황": its name with the live dot (the row opens the host), its repair or poll banner, and
 * its workspaces with their panes, as the Mac and Windows sidebars list each computer.
 */
function HostSection({ host }: { host: PairedHost }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const state = useHostState(host.id);
  const poll = useDashboardStore((store) => store.polls[host.id]);
  const reachability = useHostsStore((store) => store.status[host.id]?.reachability ?? 'unknown');
  const name = state?.hostName || host.name;
  const needsRepair = poll?.needsRepair || reachability === 'unauthorized';
  return (
    <View
      accessibilityLabel={t('phone.dashboard.hostChip', { name, status: t(reachabilityKeys[reachability]) })}
      style={styles.section}
    >
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t('phone.sessions.openHost', { name })}
        accessibilityHint={t(reachabilityKeys[reachability])}
        hitSlop={{ bottom: 8, top: 8 }}
        onPress={() => router.push(`/host/${host.id}`)}
        style={({ pressed }) => [styles.hostRow, pressed && styles.pressed]}
      >
        <LiveDot palette={palette} reachability={reachability} />
        <Text numberOfLines={1} style={styles.hostName}>
          {name}
        </Text>
        <View style={styles.chevron}>
          <Icon name="arrow" color={palette.textFaint} size={16} strokeWidth={2.4} />
        </View>
      </Pressable>
      {needsRepair ? (
        <ErrorBanner message={describeRepairNeeded(poll?.error)} />
      ) : poll?.error ? (
        <ErrorBanner message={poll.error} />
      ) : null}
      <HostWorkspaces
        hostId={host.id}
        state={state}
        loading={poll?.loading ?? true}
        glance
        onCreated={() => refreshHostPoll(host.id)}
      />
    </View>
  );
}

/**
 * "현황": every paired computer at once — the counts of running, waiting and finished panes summed over all of them,
 * then one section per computer with its workspaces and panes.
 */
export default function DashboardTab() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const hosts = useHostsStore((store) => store.hosts);
  const loaded = useHostsStore((store) => store.loaded);
  const states = useLiveStore((store) => store.states);
  const refreshing = useDashboardStore((store) =>
    hosts.some((host) => store.polls[host.id]?.loading === true),
  );
  const stats = useMemo(() => sumStats(hosts.map((host) => countStats(states[host.id]))), [hosts, states]);

  return (
    <View style={styles.screen}>
      <ScrollView
        contentContainerStyle={[
          styles.content,
          { paddingTop: insets.top + spacing.sm, paddingBottom: spacing.xxl },
        ]}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => hosts.forEach((host) => refreshHostPoll(host.id))}
            tintColor={palette.accent}
          />
        }
      >
        <View style={styles.topRow}>
          <View style={styles.titleWrap}>
            <ScreenTitle title={t('phone.dashboard.title')} />
          </View>
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

        {hosts.length > 0 ? (
          <View style={styles.stats}>
            <StatTile count={stats.running} label={t('phone.dashboard.stat.running')} tone="run" wide />
            <StatTile count={stats.waiting} label={t('phone.dashboard.stat.waiting')} tone="wait" />
            <StatTile count={stats.done} label={t('phone.dashboard.stat.done')} tone="done" quiet />
          </View>
        ) : null}

        {hosts.length > 0 ? (
          hosts.map((host) => <HostSection key={host.id} host={host} />)
        ) : loaded ? (
          <EmptyState
            title={t('phone.hosts.empty.title')}
            description={t('phone.hosts.empty.description')}
            icon="hosts"
            action={{ label: t('phone.hosts.addHost'), onPress: () => router.push('/pair') }}
          />
        ) : null}
      </ScrollView>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    content: { gap: spacing.lg, paddingHorizontal: layout.gutter },
    topRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.md, justifyContent: 'space-between' },
    titleWrap: { flex: 1, flexShrink: 1 },
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
    section: { gap: spacing.sm, marginTop: spacing.sm },
    hostRow: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.sm,
      paddingHorizontal: spacing.xs,
      paddingVertical: spacing.xs,
    },
    hostName: {
      color: palette.text,
      flex: 1,
      fontFamily: headingFontFamily,
      fontSize: 20,
      fontWeight: '700',
      letterSpacing: -0.3,
    },
    chevron: { opacity: 0.9 },
  });
