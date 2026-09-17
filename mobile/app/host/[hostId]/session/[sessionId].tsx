import { useCallback, useEffect, useRef, useState } from 'react';
import {
  FlatList,
  KeyboardAvoidingView,
  Platform,
  StyleSheet,
  Text,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeError } from '@/api/client';
import type { LogEntry, MobileSessionDetail, QuestionAnswers, SubmitAccepted } from '@/api/types';
import { Composer } from '@/components/composer';
import { LogEntryView } from '@/components/log-entry-view';
import { PermissionCard } from '@/components/permission-card';
import { SessionHeader } from '@/components/session-header';
import { EmptyState, ErrorBanner } from '@/components/ui';
import { useLongPoll } from '@/hooks/use-long-poll';
import { useHostClient, useLiveStore, useSessionDetail } from '@/store/live';
import { showToast } from '@/store/toast';
import { colors, monoText, spacing } from '@/theme';

const acceptedMessages: Record<SubmitAccepted, string> = {
  started: '전송',
  steered: '실행 중인 작업에 전달',
  queued: '대기열에 추가',
};

export default function SessionScreen() {
  const { hostId, sessionId } = useLocalSearchParams<{ hostId: string; sessionId: string }>();
  const client = useHostClient(hostId);
  const detail = useSessionDetail(hostId, sessionId);
  const applyDetail = useLiveStore((store) => store.applyDetail);
  const insets = useSafeAreaInsets();

  const listRef = useRef<FlatList<LogEntry>>(null);
  const atBottom = useRef(true);
  const [sending, setSending] = useState(false);
  const [deciding, setDeciding] = useState(false);

  const fetchPage = useCallback(
    (since: number | undefined, signal: AbortSignal) => {
      if (!client || !sessionId) return Promise.reject(new Error('세션을 찾을 수 없습니다.'));
      return client.session(sessionId, { since, wait: since === undefined ? 0 : 10, signal });
    },
    [client, sessionId],
  );

  const onData = useCallback(
    (data: MobileSessionDetail) => {
      if (hostId && sessionId) applyDetail(hostId, sessionId, data);
    },
    [applyDetail, hostId, sessionId],
  );

  const poll = useLongPoll<MobileSessionDetail>({
    enabled: Boolean(client && sessionId),
    fetchPage,
    revisionOf: (data) => data.revision,
    onData,
  });

  const entryCount = detail?.entries.length ?? 0;
  const lastEntryText = detail?.entries[entryCount - 1]?.text ?? '';

  useEffect(() => {
    if (!atBottom.current || entryCount === 0) return;
    const timer = setTimeout(() => listRef.current?.scrollToEnd({ animated: true }), 40);
    return () => clearTimeout(timer);
  }, [entryCount, lastEntryText]);

  const onScroll = useCallback((event: NativeSyntheticEvent<NativeScrollEvent>) => {
    const { contentOffset, contentSize, layoutMeasurement } = event.nativeEvent;
    const distanceFromBottom = contentSize.height - layoutMeasurement.height - contentOffset.y;
    atBottom.current = distanceFromBottom < 80;
  }, []);

  const send = useCallback(
    async (text: string) => {
      if (!client || !sessionId) return;
      setSending(true);
      try {
        const result = await client.submit(sessionId, text);
        showToast(acceptedMessages[result.accepted], 'success');
        atBottom.current = true;
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setSending(false);
      }
    },
    [client, poll, sessionId],
  );

  const stop = useCallback(() => {
    if (!client || !sessionId) return;
    void (async () => {
      try {
        await client.stop(sessionId);
        showToast('중지 요청됨');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      }
    })();
  }, [client, poll, sessionId]);

  const decide = useCallback(
    async (requestId: string, runId: string, allow: boolean) => {
      if (!client || !sessionId) return;
      setDeciding(true);
      try {
        await client.respondPermission(sessionId, { requestId, runId, allow });
        showToast(allow ? '허용했습니다' : '거부했습니다', 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setDeciding(false);
      }
    },
    [client, poll, sessionId],
  );

  const answer = useCallback(
    async (requestId: string, runId: string, answers: QuestionAnswers) => {
      if (!client || !sessionId) return;
      setDeciding(true);
      try {
        await client.answer(sessionId, { requestId, runId, answers });
        showToast('답변을 보냈습니다', 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setDeciding(false);
      }
    },
    [client, poll, sessionId],
  );

  const session = detail?.session;

  return (
    <KeyboardAvoidingView
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 96 : 0}
      style={styles.screen}
    >
      <Stack.Screen options={{ title: session?.title || '세션' }} />

      {poll.needsRepair ? (
        <ErrorBanner message="재페어링 필요 — 저장된 키가 호스트와 일치하지 않습니다." />
      ) : poll.error ? (
        <ErrorBanner message={poll.error} />
      ) : null}

      <FlatList
        ref={listRef}
        data={detail?.entries ?? []}
        keyExtractor={(entry) => entry.id}
        renderItem={({ item }) => <LogEntryView entry={item} />}
        contentContainerStyle={styles.list}
        onScroll={onScroll}
        scrollEventThrottle={64}
        keyboardShouldPersistTaps="handled"
        ListHeaderComponent={detail ? <SessionHeader detail={detail} /> : null}
        ListEmptyComponent={
          <EmptyState title={poll.loading ? '불러오는 중…' : '기록이 없습니다'} />
        }
        ListFooterComponent={
          detail ? (
            <View style={styles.footer}>
              {detail.permissions.map((permission) => (
                <PermissionCard
                  key={permission.id}
                  permission={permission}
                  busy={deciding}
                  onDecide={(allow) => void decide(permission.id, permission.runId, allow)}
                  onAnswer={(answers) => void answer(permission.id, permission.runId, answers)}
                />
              ))}

              {detail.queued.length > 0 ? (
                <View style={styles.queued}>
                  <Text style={styles.queuedTitle}>대기열 {detail.queued.length}건</Text>
                  {detail.queued.map((item) => (
                    <Text key={item.id} numberOfLines={2} style={styles.queuedItem}>
                      · {item.text}
                    </Text>
                  ))}
                </View>
              ) : null}
            </View>
          ) : null
        }
      />

      <View style={{ paddingBottom: insets.bottom + spacing.sm }}>
        <Composer
          disabled={!client || session === undefined || sending}
          terminal={session?.terminal ?? false}
          running={session?.status === 'running'}
          sending={sending}
          onSend={send}
          onStop={stop}
        />
      </View>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: colors.background, flex: 1 },
  list: { padding: spacing.lg, paddingBottom: spacing.xl },
  footer: { gap: spacing.sm, paddingTop: spacing.sm },
  queued: {
    backgroundColor: colors.surface,
    borderRadius: spacing.sm,
    gap: spacing.xs,
    padding: spacing.md,
  },
  queuedTitle: { color: colors.textMuted, fontSize: 13, fontWeight: '700' },
  queuedItem: { ...monoText, color: colors.textFaint },
});
