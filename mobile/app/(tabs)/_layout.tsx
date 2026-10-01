import { useCallback, useEffect, useMemo } from 'react';
import { router, useFocusEffect } from 'expo-router';
import { appForeground } from '@/api/relay/foreground';
import { Tabs } from 'expo-router/js-tabs';
import { HostPoller } from '@/components/host-poller';
import { Icon } from '@/components/icons';
import { actionCount, buildAttentionList } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { needsOnboarding } from '@/lib/onboarding';
import { retryUnreachableHosts, unreachableHostIds } from '@/lib/offline-retry';
import { useHostsStore } from '@/store/hosts';
import { useLiveStore } from '@/store/live';
import { usePalette } from '@/theme';

/**
 * The bottom tab bar — 현황 · 세션 · 알림 · 호스트 — under the root stack. A pane, its
 * files and the pairing screens are pushed above the tabs, so the bar never sits under
 * the composer. Every paired host's pane list is polled here while the tabs show.
 */
export default function TabsLayout() {
  const palette = usePalette();
  const hosts = useHostsStore((store) => store.hosts);
  const loaded = useHostsStore((store) => store.loaded);
  const states = useLiveStore((store) => store.states);
  const badge = useMemo(() => actionCount(buildAttentionList(hosts, states)), [hosts, states]);

  const refreshAll = useHostsStore((store) => store.refreshAll);

  // A phone with no Mac yet opens the connection guide over the tabs, so pairing can
  // come back down to them (`pair.tsx` returns to "/" before opening the host).
  useEffect(() => {
    if (loaded && needsOnboarding(hosts.length)) {
      router.push('/connect');
    }
  }, [loaded, hosts.length]);

  // Every tab shows a host's live dot, so reachability is kept fresh for all of them:
  // what each host's dot says was true when the app went away, so ask again when it
  // comes back, and a host that dropped off for a moment (the Mac changed networks or
  // slept) comes back by itself instead of waiting for a pull.
  useFocusEffect(
    useCallback(() => appForeground.subscribe(() => void refreshAll()), [refreshAll]),
  );
  useFocusEffect(
    useCallback(
      () =>
        retryUnreachableHosts({
          unreachable: () => {
            const state = useHostsStore.getState();
            return unreachableHostIds(state.hosts, state.status);
          },
          refresh: (id) => useHostsStore.getState().refreshReachability(id, { quiet: true }),
          subscribe: (listener) => useHostsStore.subscribe(listener),
        }),
      [],
    ),
  );

  return (
    <>
      {hosts.map((host) => (
        <HostPoller key={host.id} hostId={host.id} />
      ))}
      <Tabs
        screenOptions={{
          headerShown: false,
          sceneStyle: { backgroundColor: palette.background },
          tabBarActiveTintColor: palette.accent,
          tabBarInactiveTintColor: palette.textFaint,
          tabBarLabelStyle: { fontSize: 10.5, fontWeight: '700' },
          tabBarStyle: {
            backgroundColor: palette.surface,
            borderTopColor: palette.border,
          },
          tabBarBadgeStyle: {
            backgroundColor: palette.wait,
            color: palette.onBadge,
            fontSize: 10,
            fontWeight: '800',
          },
        }}
      >
        <Tabs.Screen
          name="index"
          options={{
            title: t('phone.tabs.dashboard'),
            tabBarIcon: ({ color }) => <Icon name="dashboard" color={color} />,
          }}
        />
        <Tabs.Screen
          name="sessions"
          options={{
            title: t('phone.tabs.sessions'),
            tabBarIcon: ({ color }) => <Icon name="sessions" color={color} />,
          }}
        />
        <Tabs.Screen
          name="alerts"
          options={{
            title: t('phone.tabs.alerts'),
            tabBarIcon: ({ color }) => <Icon name="alerts" color={color} />,
            ...(badge > 0
              ? {
                  tabBarBadge: badge,
                  tabBarAccessibilityLabel: t('phone.tabs.alertsLabel', { count: badge }),
                }
              : {}),
          }}
        />
        <Tabs.Screen
          name="hosts"
          options={{
            title: t('phone.tabs.hosts'),
            tabBarIcon: ({ color }) => <Icon name="hosts" color={color} />,
          }}
        />
      </Tabs>
    </>
  );
}
