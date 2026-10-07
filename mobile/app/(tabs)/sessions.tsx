import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { LiveDot, reachabilityKeys } from '@/components/host-status';
import { HostWorkspaces } from '@/components/host-workspaces';
import { Icon } from '@/components/icons';
import { EmptyState, ScreenTitle } from '@/components/ui';
import { t } from '@/lib/i18n';
import { refreshHostPoll, useDashboardStore } from '@/store/dashboard';
import { useHostsStore, type PairedHost } from '@/store/hosts';
import { useHostState } from '@/store/live';
import { headingFontFamily, spacing, useStyles, usePalette, type Palette } from '@/theme';

function HostSection({ host }: { host: PairedHost }) {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const state = useHostState(host.id);
  const loading = useDashboardStore((store) => store.polls[host.id]?.loading ?? true);
  const reachability = useHostsStore((store) => store.status[host.id]?.reachability ?? 'unknown');
  const name = state?.hostName || host.name;

  return (
    <View style={styles.section}>
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
      <HostWorkspaces hostId={host.id} state={state} loading={loading} onCreated={() => refreshHostPoll(host.id)} />
    </View>
  );
}

/** "세션": every paired host's workspaces and their panes as rows, one host after another. */
export default function SessionsTab() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const insets = useSafeAreaInsets();
  const hosts = useHostsStore((store) => store.hosts);
  const loaded = useHostsStore((store) => store.loaded);
  const refreshing = useDashboardStore((store) =>
    Object.values(store.polls).some((poll) => poll.loading),
  );

  return (
    <ScrollView
      style={styles.screen}
      contentContainerStyle={[
        styles.content,
        { paddingTop: insets.top + spacing.md, paddingBottom: spacing.xxl },
      ]}
      refreshControl={
        <RefreshControl
          refreshing={refreshing}
          onRefresh={() => hosts.forEach((host) => refreshHostPoll(host.id))}
          tintColor={palette.accent}
        />
      }
    >
      <ScreenTitle title={t('phone.tabs.sessions')} style={styles.title} />
      {hosts.length === 0 && loaded ? (
        <EmptyState
          title={t('phone.hosts.empty.title')}
          description={t('phone.hosts.empty.description')}
        />
      ) : (
        hosts.map((host) => <HostSection key={host.id} host={host} />)
      )}
    </ScrollView>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    content: { gap: spacing.xl, paddingHorizontal: spacing.lg },
    title: { paddingHorizontal: spacing.xs },
    section: { gap: spacing.sm },
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
    pressed: { opacity: 0.7 },
  });
