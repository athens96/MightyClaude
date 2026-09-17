import { useCallback, useMemo, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, router, useLocalSearchParams } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeError } from '@/api/client';
import type { MobileState } from '@/api/types';
import { Button, EmptyState, ErrorBanner } from '@/components/ui';
import { NewSessionSheet, type NewSessionChoice } from '@/components/new-session-sheet';
import { SessionRow } from '@/components/session-row';
import { useLongPoll } from '@/hooks/use-long-poll';
import { groupSessionsByWorkspace } from '@/lib/merge';
import { useHostsStore } from '@/store/hosts';
import { useHostClient, useHostState, useLiveStore } from '@/store/live';
import { showToast } from '@/store/toast';
import { colors, spacing } from '@/theme';

export default function HostScreen() {
  const { hostId } = useLocalSearchParams<{ hostId: string }>();
  const host = useHostsStore((state) => state.hosts.find((entry) => entry.id === hostId));
  const client = useHostClient(hostId);
  const state = useHostState(hostId);
  const applyState = useLiveStore((store) => store.applyState);
  const insets = useSafeAreaInsets();
  const [creatingFor, setCreatingFor] = useState<string | undefined>(undefined);
  const [creating, setCreating] = useState(false);

  const fetchPage = useCallback(
    (since: number | undefined, signal: AbortSignal) => {
      if (!client) return Promise.reject(new Error('호스트를 찾을 수 없습니다.'));
      return client.state({ since, wait: since === undefined ? 0 : 10, signal });
    },
    [client],
  );

  const onData = useCallback(
    (data: MobileState) => {
      if (hostId) applyState(hostId, data);
    },
    [applyState, hostId],
  );

  const poll = useLongPoll<MobileState>({
    enabled: Boolean(client && hostId),
    fetchPage,
    revisionOf: (data) => data.revision,
    onData,
  });

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

      {poll.needsRepair ? (
        <ErrorBanner message="재페어링 필요 — 저장된 키가 호스트와 일치하지 않습니다." />
      ) : poll.error ? (
        <ErrorBanner message={poll.error} />
      ) : null}

      <ScrollView
        contentContainerStyle={[styles.content, { paddingBottom: insets.bottom + spacing.xl }]}
        refreshControl={
          <RefreshControl
            refreshing={poll.loading}
            onRefresh={poll.refresh}
            tintColor={colors.accent}
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
                    {group.workspace.remote ? ' · 원격' : ''}
                  </Text>
                  <Text numberOfLines={1} style={styles.workspacePath}>
                    {group.workspace.path}
                  </Text>
                </View>
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
                    onPress={() => router.push(`/host/${host.id}/session/${session.id}`)}
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

const styles = StyleSheet.create({
  screen: { backgroundColor: colors.background, flex: 1 },
  content: { gap: spacing.xl, padding: spacing.lg },
  group: { gap: spacing.sm },
  groupHeader: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
  groupTitles: { flex: 1 },
  workspaceName: { color: colors.text, fontSize: 15, fontWeight: '700' },
  workspacePath: { color: colors.textFaint, fontSize: 12 },
  noSessions: { color: colors.textFaint, fontSize: 13, paddingVertical: spacing.sm },
});
