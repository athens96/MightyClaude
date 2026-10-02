import { useCallback } from 'react';
import { RefreshControl, ScrollView, StyleSheet, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeRepairNeeded, type RelayState } from '@/api/relay/transport';
import type { MobileState } from '@/api/types';
import { HostWorkspaces } from '@/components/host-workspaces';
import { ScreenShareEntry } from '@/components/screen-share-entry';
import { EmptyState, ErrorBanner } from '@/components/ui';
import { useLongPoll } from '@/hooks/use-long-poll';
import { t } from '@/lib/i18n';
import { useForgetRefusedSecret, useHostsStore } from '@/store/hosts';
import { useHostClient, useHostState, useLiveStore } from '@/store/live';
import { spacing, useStyles, usePalette, type Palette } from '@/theme';

/**
 * One host's workspaces and panes — the screen a pairing lands on and the host list
 * opens. The tabs show the same cards; this stack screen keeps the `/host/{id}` link.
 */

export default function HostScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { hostId } = useLocalSearchParams<{ hostId: string }>();
  const host = useHostsStore((state) => state.hosts.find((entry) => entry.id === hostId));
  const client = useHostClient(hostId);
  const state = useHostState(hostId);
  const applyState = useLiveStore((store) => store.applyState);
  const insets = useSafeAreaInsets();

  const fetchPage = useCallback(
    (since: number | undefined, signal: AbortSignal) => {
      if (!client) return Promise.reject(new Error(t('phone.workspaces.hostNotFound.title')));
      return client.state({ since, wait: since === undefined ? 0 : 10, signal });
    },
    [client],
  );

  const onData = useCallback(
    (data: MobileState, fresh: boolean) => {
      if (hostId) applyState(hostId, data, fresh);
    },
    [applyState, hostId],
  );

  const subscribe = useCallback(
    (onChange: (revision: number) => void) => {
      if (!client) return () => undefined;
      return client.onNotify((event) => {
        if (event.scope === 'state') onChange(event.revision);
      });
    },
    [client],
  );

  const watchLink = useCallback(
    (listener: (state: RelayState) => void) => client?.onStateChange(listener) ?? (() => undefined),
    [client],
  );

  const poll = useLongPoll<MobileState>({
    enabled: Boolean(client && hostId),
    fetchPage,
    revisionOf: (data) => data.revision,
    onData,
    subscribe,
    watchLink,
  });

  // A refused secret is never presented again: it is dropped the moment the host says so.
  useForgetRefusedSecret(hostId, poll.needsRepair, poll.error);
  const needsRepair = useHostsStore(
    (store) => store.status[hostId ?? '']?.reachability === 'unauthorized',
  );

  if (!host) {
    return (
      <View style={styles.screen}>
        <EmptyState
          title={t('phone.workspaces.hostNotFound.title')}
          description={t('phone.workspaces.hostNotFound.description')}
        />
      </View>
    );
  }

  return (
    <View style={styles.screen}>
      <Stack.Screen options={{ title: state?.hostName ?? host.name }} />

      {poll.needsRepair || needsRepair ? (
        <ErrorBanner message={describeRepairNeeded(poll.error)} />
      ) : poll.error ? (
        <ErrorBanner message={poll.error} />
      ) : null}

      <View style={styles.actions}>
        <ScreenShareEntry hostId={host.id} hostName={state?.hostName ?? host.name} />
      </View>

      <ScrollView
        contentContainerStyle={[styles.content, { paddingBottom: insets.bottom + spacing.xl }]}
        refreshControl={
          <RefreshControl
            refreshing={poll.loading}
            onRefresh={poll.refresh}
            tintColor={palette.accent}
          />
        }
      >
        <HostWorkspaces hostId={host.id} state={state} loading={poll.loading} onCreated={poll.refresh} />
      </ScrollView>
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    content: { paddingHorizontal: spacing.md + 2, paddingTop: spacing.sm },
    actions: {
      alignItems: 'flex-start',
      paddingHorizontal: spacing.md + 2,
      paddingTop: spacing.sm,
    },
  });
