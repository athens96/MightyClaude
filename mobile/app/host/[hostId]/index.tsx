import { useCallback, useMemo, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, router, useLocalSearchParams } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeError } from '@/api/client';
import { describeRepairNeeded, type RelayState } from '@/api/relay/transport';
import { isAgentIOPane, type MobileState } from '@/api/types';
import { Button, EmptyState, ErrorBanner } from '@/components/ui';
import { NewSessionSheet, type NewSessionChoice } from '@/components/new-session-sheet';
import { SessionRow } from '@/components/session-row';
import { useCapabilities } from '@/hooks/use-capabilities';
import { useLongPoll } from '@/hooks/use-long-poll';
import { hasCapability } from '@/lib/capabilities';
import { t } from '@/lib/i18n';
import { groupSessionsByWorkspace } from '@/lib/merge';
import { useForgetRefusedSecret, useHostsStore } from '@/store/hosts';
import { useHostClient, useHostState, useLiveStore } from '@/store/live';
import { showToast } from '@/store/toast';
import { spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

export default function HostScreen() {
  const palette = usePalette();
  const styles = useStyles(makeStyles);
  const { hostId } = useLocalSearchParams<{ hostId: string }>();
  const host = useHostsStore((state) => state.hosts.find((entry) => entry.id === hostId));
  const client = useHostClient(hostId);
  const state = useHostState(hostId);
  const applyState = useLiveStore((store) => store.applyState);
  const insets = useSafeAreaInsets();
  const [creatingFor, setCreatingFor] = useState<string | undefined>(undefined);
  const [creating, setCreating] = useState(false);
  // A Mac older than the files routes never shows the button.
  const canBrowseFiles = hasCapability(useCapabilities(hostId, client), 'files');

  const fetchPage = useCallback(
    (since: number | undefined, signal: AbortSignal) => {
      if (!client) return Promise.reject(new Error('호스트를 찾을 수 없습니다.'));
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

  const groups = useMemo(() => (state ? groupSessionsByWorkspace(state) : []), [state]);
  const activeWorkspace = groups.find((group) => group.workspace.id === creatingFor);

  const createSession = useCallback(
    async (choice: NewSessionChoice) => {
      if (!client || !creatingFor || !hostId) return;
      setCreating(true);
      try {
        const created = await client.createSession(creatingFor, choice);
        setCreatingFor(undefined);
        poll.refresh();
        router.push(`/host/${hostId}/session/${created.sessionId}`);
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setCreating(false);
      }
    },
    [client, creatingFor, hostId, poll],
  );

  if (!host) {
    return (
      <View style={styles.screen}>
        <EmptyState title="호스트를 찾을 수 없습니다" description="호스트 목록에서 다시 선택하세요." />
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
        {groups.length === 0 ? (
          <EmptyState
            title={poll.loading ? '불러오는 중…' : '작업 공간이 없습니다'}
            description="데스크톱에서 작업 공간을 연 뒤 다시 확인하세요."
          />
        ) : (
          groups.map((group) => (
            <View key={group.workspace.id} style={styles.group}>
              <View style={styles.groupHeader}>
                <View style={styles.groupTitles}>
                  <Text numberOfLines={1} style={styles.workspaceName}>
                    {group.workspace.name}
                  </Text>
                  <Text numberOfLines={1} style={styles.workspacePath}>
                    {group.workspace.path}
                  </Text>
                </View>
                {canBrowseFiles ? (
                  <Button
                    label={t('phone.files.open')}
                    accessibilityLabel={t('phone.files.openLabel', { name: group.workspace.name })}
                    compact
                    onPress={() => router.push(`/host/${host.id}/workspace/${group.workspace.id}/files`)}
                  />
                ) : null}
                <Button
                  label="새 창"
                  compact
                  tone="primary"
                  onPress={() => setCreatingFor(group.workspace.id)}
                />
              </View>

              {group.sessions.length === 0 ? (
                <Text style={styles.noSessions}>열린 세션이 없습니다.</Text>
              ) : (
                group.sessions.map((session) => (
                  <SessionRow
                    key={session.id}
                    session={session}
                    onPress={() =>
                      // An agent's own terminal/browser pane is listed, not opened:
                      // its contents live on the Mac and the host keeps no record
                      // to show here.
                      isAgentIOPane(session.kind)
                        ? showToast('에이전트 창이 여는 창이라 Mac에서만 볼 수 있습니다.')
                        : router.push(`/host/${host.id}/session/${session.id}`)
                    }
                  />
                ))
              )}
            </View>
          ))
        )}
      </ScrollView>

      <NewSessionSheet
        visible={creatingFor !== undefined}
        workspaceName={activeWorkspace?.workspace.name ?? ''}
        busy={creating}
        onCancel={() => setCreatingFor(undefined)}
        onCreate={(choice) => void createSession(choice)}
      />
    </View>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    content: { gap: spacing.xxl, paddingHorizontal: spacing.lg, paddingTop: spacing.md },
    group: { gap: 0 },
    // A workspace is a section heading in the serif; its panes are the lines under it.
    groupHeader: {
      alignItems: 'center',
      flexDirection: 'row',
      gap: spacing.sm,
      paddingBottom: spacing.sm,
    },
    groupTitles: { flex: 1, gap: 2 },
    workspaceName: { ...typeScale.heading, color: palette.text },
    workspacePath: { color: palette.textFaint, fontSize: 12 },
    noSessions: {
      borderTopColor: palette.border,
      borderTopWidth: StyleSheet.hairlineWidth,
      color: palette.textFaint,
      fontSize: 13,
      paddingVertical: spacing.md,
    },
  });
