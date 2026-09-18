import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { Stack, router, useFocusEffect, useLocalSearchParams } from 'expo-router';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeError, isNotFound } from '@/api/client';
import {
  ENTRY_PAGE_SIZE,
  type LogEntry,
  type MessageCommandAction,
  type MobileCommand,
  type MobileSessionDetail,
  type QuestionAnswers,
  type SettingsPatch,
  type SubmitMode,
} from '@/api/types';
import { Composer } from '@/components/composer';
import { LogEntryView } from '@/components/log-entry-view';
import { PermissionCard } from '@/components/permission-card';
import { QueuedList } from '@/components/queued-list';
import {
  SessionHeader,
  optionsFor,
  settingTitles,
  valueFor,
  type SettingField,
} from '@/components/session-header';
import { ConfirmDialog, MessageSheet, PickerSheet, PromptDialog, Sheet } from '@/components/sheets';
import { Button, EmptyState, ErrorBanner } from '@/components/ui';
import { useCapabilities } from '@/hooks/use-capabilities';
import { useLongPoll } from '@/hooks/use-long-poll';
import { hasCapability } from '@/lib/capabilities';
import { commandActionOf, isMessageAction } from '@/lib/commands';
import {
  combineEntries,
  isHistoryExhausted,
  oldestEntryId,
  prependOlderPage,
  retainDropped,
} from '@/lib/history';
import { useHostClient, useLiveStore, useSessionCommands, useSessionDetail } from '@/store/live';
import { showToast } from '@/store/toast';
import { spacing, useStyles, type Palette } from '@/theme';

/**
 * The host reports what actually happened, not what was asked for: "다음 요청" on a pane
 * that has just gone idle is answered with `started`. The toast therefore follows the
 * answer and never the button; an unknown word falls back to plain "전송".
 */
const acceptedMessages: Record<string, string> = {
  started: '전송',
  steered: '실행 중인 작업에 전달',
  queued: '대기열에 추가',
};

const NO_ENTRIES: LogEntry[] = [];

function patchFor(field: SettingField, id: string): SettingsPatch {
  switch (field) {
    case 'model':
      return { model: id };
    case 'permissionMode':
      return { permissionMode: id };
    case 'effort':
      return { effort: id };
    case 'agentViewMode':
      return { agentViewMode: id };
    case 'mightyStyle':
      return { mightyStyle: id };
  }
}

export default function SessionScreen() {
  const styles = useStyles(makeStyles);
  const { hostId, sessionId } = useLocalSearchParams<{ hostId: string; sessionId: string }>();
  const client = useHostClient(hostId);
  const detail = useSessionDetail(hostId, sessionId);
  const applyDetail = useLiveStore((store) => store.applyDetail);
  const setCommands = useLiveStore((store) => store.setCommands);
  const commands = useSessionCommands(hostId, sessionId);
  const capabilities = useCapabilities(hostId, client);
  const insets = useSafeAreaInsets();

  const listRef = useRef<FlatList<LogEntry>>(null);
  const atBottom = useRef(true);
  const [sending, setSending] = useState(false);
  const [deciding, setDeciding] = useState(false);
  const [paneBusy, setPaneBusy] = useState(false);
  const [queueBusy, setQueueBusy] = useState(false);
  const [savingSetting, setSavingSetting] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [closing, setClosing] = useState(false);
  const [picker, setPicker] = useState<SettingField | undefined>(undefined);
  /** Set while the view mode waits for a style, so both go to the host in one POST. */
  const [pendingViewMode, setPendingViewMode] = useState<string | undefined>(undefined);
  const [message, setMessage] = useState<{ title: string; body: string } | undefined>(undefined);
  // The pane is gone — closed here or elsewhere. The long poll stops before we leave, so
  // the screen on its way out never raises a banner about a pane that no longer exists.
  const [closed, setClosed] = useState(false);
  const closedRef = useRef(false);
  const commandRunning = useRef(false);

  // History paged in with `entries?before=`; kept apart from the long-poll window so an
  // entry arriving while a page loads can neither duplicate nor reorder what is shown.
  const [older, setOlder] = useState<LogEntry[]>([]);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const loadingOlderRef = useRef(false);
  const [exhausted, setExhausted] = useState(false);

  const canQueue = hasCapability(capabilities, 'queue');
  const canPane = hasCapability(capabilities, 'pane');
  const canHistory = hasCapability(capabilities, 'history');
  const canSettings = hasCapability(capabilities, 'settings');
  const canCommands = hasCapability(capabilities, 'commands');

  /** Back to where we came from, or to the host when this screen was deep-linked into. */
  const leavePane = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace(`/host/${hostId}`);
  }, [hostId]);

  /** The host no longer knows this pane: say so once, quietly, and leave. */
  const paneGone = useCallback(() => {
    if (closedRef.current) return;
    closedRef.current = true;
    setClosed(true);
    showToast('실행 창이 닫혔습니다');
    leavePane();
  }, [leavePane]);

  const fetchPage = useCallback(
    async (since: number | undefined, signal: AbortSignal) => {
      if (!client || !sessionId) throw new Error('세션을 찾을 수 없습니다.');
      try {
        return await client.session(sessionId, {
          since,
          wait: since === undefined ? 0 : 10,
          signal,
        });
      } catch (error) {
        // A pane closed on the Mac answers 404 forever; leave instead of backing off.
        if (isNotFound(error)) paneGone();
        throw error;
      }
    },
    [client, paneGone, sessionId],
  );

  const onData = useCallback(
    (data: MobileSessionDetail) => {
      if (hostId && sessionId) applyDetail(hostId, sessionId, data);
    },
    [applyDetail, hostId, sessionId],
  );

  const subscribe = useCallback(
    (onChange: (revision: number) => void) => {
      if (!client || !sessionId) return () => undefined;
      const scope = `session:${sessionId}`;
      return client.onNotify((event) => {
        if (event.scope === scope) onChange(event.revision);
      });
    },
    [client, sessionId],
  );

  const poll = useLongPoll<MobileSessionDetail>({
    enabled: Boolean(client && sessionId) && !closed,
    fetchPage,
    revisionOf: (data) => data.revision,
    onData,
    subscribe,
  });

  const session = detail?.session;
  const live = detail?.entries ?? NO_ENTRIES;
  const liveRef = useRef(live);
  liveRef.current = live;
  // The window the long poll last showed, so the entries it drops can be kept.
  const previousLive = useRef<LogEntry[]>(NO_ENTRIES);

  const entries = useMemo(() => combineEntries(older, live), [older, live]);
  const entryCount = entries.length;
  const lastEntryText = entries[entryCount - 1]?.text ?? '';

  useEffect(() => {
    setOlder([]);
    setExhausted(false);
    previousLive.current = NO_ENTRIES;
    closedRef.current = false;
    setClosed(false);
  }, [sessionId]);

  // The host sends only the newest entries and replaces that window wholesale, so what
  // slides off its front is caught here: no `before=` page could ever bring it back.
  useEffect(() => {
    const previous = previousLive.current;
    previousLive.current = live;
    if (previous.length === 0 || previous === live) return;
    setOlder((prev) => retainDropped(prev, previous, live));
  }, [live]);

  useEffect(() => {
    if (!atBottom.current || entryCount === 0) return;
    const timer = setTimeout(() => listRef.current?.scrollToEnd({ animated: true }), 40);
    return () => clearTimeout(timer);
  }, [entryCount, lastEntryText]);

  // Slash commands are cached per session and re-read whenever the screen regains focus.
  useFocusEffect(
    useCallback(() => {
      if (!client || !hostId || !sessionId || !canCommands) return undefined;
      const controller = new AbortController();
      void client
        .commands(sessionId, controller.signal)
        .then((result) => {
          if (!controller.signal.aborted) setCommands(hostId, sessionId, result.commands ?? []);
        })
        .catch(() => undefined);
      return () => controller.abort();
    }, [canCommands, client, hostId, sessionId, setCommands]),
  );

  const canLoadOlder = canHistory && (detail?.hasOlder ?? false) && !exhausted;

  const loadOlder = useCallback(async () => {
    // Scrolling fires far faster than a page arrives, so the guard has to be the ref:
    // `loadingOlder` is whatever the render that built this callback captured.
    if (!client || !sessionId || !canLoadOlder || loadingOlderRef.current) return;
    const before = oldestEntryId(older, liveRef.current);
    if (!before) return;
    loadingOlderRef.current = true;
    setLoadingOlder(true);
    try {
      const page = await client.entries(sessionId, { before, limit: ENTRY_PAGE_SIZE });
      const fetched = page.entries ?? [];
      setOlder((prev) => prependOlderPage(prev, liveRef.current, fetched));
      if (isHistoryExhausted({ entries: fetched, hasMore: page.hasMore })) setExhausted(true);
    } catch (error) {
      showToast(describeError(error), 'error');
    } finally {
      loadingOlderRef.current = false;
      setLoadingOlder(false);
    }
  }, [canLoadOlder, client, older, sessionId]);

  const onScroll = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      const { contentOffset, contentSize, layoutMeasurement } = event.nativeEvent;
      const distanceFromBottom = contentSize.height - layoutMeasurement.height - contentOffset.y;
      atBottom.current = distanceFromBottom < 80;
      if (contentOffset.y < 120) void loadOlder();
    },
    [loadOlder],
  );

  const send = useCallback(
    async (text: string, mode?: SubmitMode): Promise<boolean> => {
      if (!client || !sessionId) return false;
      setSending(true);
      try {
        const result = await client.submit(sessionId, text, mode ? { mode } : undefined);
        showToast(acceptedMessages[result.accepted] ?? '전송', 'success');
        atBottom.current = true;
        poll.refresh();
        return true;
      } catch (error) {
        showToast(describeError(error), 'error');
        return false;
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

  const removeQueued = useCallback(
    (itemId: string) => {
      if (!client || !sessionId) return;
      void (async () => {
        setQueueBusy(true);
        try {
          await client.removeQueued(sessionId, itemId);
          showToast('대기열에서 뺐습니다', 'success');
          poll.refresh();
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          setQueueBusy(false);
        }
      })();
    },
    [client, poll, sessionId],
  );

  const runNext = useCallback(() => {
    if (!client || !sessionId) return;
    void (async () => {
      setQueueBusy(true);
      try {
        await client.runNext(sessionId);
        showToast('다음 요청을 시작했습니다', 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setQueueBusy(false);
      }
    })();
  }, [client, poll, sessionId]);

  const rename = useCallback(
    (title: string) => {
      if (!client || !sessionId) return;
      void (async () => {
        setPaneBusy(true);
        try {
          await client.rename(sessionId, title);
          setRenaming(false);
          showToast('이름을 바꿨습니다', 'success');
          poll.refresh();
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          setPaneBusy(false);
        }
      })();
    },
    [client, poll, sessionId],
  );

  const closePane = useCallback(() => {
    if (!client || !sessionId) return;
    void (async () => {
      setPaneBusy(true);
      try {
        await client.close(sessionId);
        setClosing(false);
        // Stop polling the pane we have just closed before leaving, so the request that
        // is still in flight cannot come back as a banner about a pane that is gone.
        closedRef.current = true;
        setClosed(true);
        showToast('실행 창을 닫았습니다', 'success');
        leavePane();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setPaneBusy(false);
      }
    })();
  }, [client, leavePane, sessionId]);

  const settings = canSettings ? detail?.settings : undefined;

  const openPicker = useCallback(
    (field: SettingField) => {
      // A host without the "settings" capability has none to offer, even when it does
      // list the /model and /permission commands that lead here.
      if (!settings) {
        showToast('호스트가 설정을 알려 주지 않았습니다.', 'error');
        return;
      }
      setPicker(field);
    },
    [settings],
  );

  const closePicker = useCallback(() => {
    setPicker(undefined);
    setPendingViewMode(undefined);
  }, []);

  const applySetting = useCallback(
    (id: string) => {
      if (!client || !sessionId || !picker) return;
      // The host offers the other Mighty styles only once the pane is in Mighty view, so
      // when there is a real choice the view is held back and both keys travel in one
      // POST; with `cli` alone there is nothing to choose and the view goes on its own.
      if (
        picker === 'agentViewMode' &&
        id === 'mighty' &&
        optionsFor(settings, 'mightyStyle').length > 1
      ) {
        setPendingViewMode(id);
        setPicker('mightyStyle');
        return;
      }
      const patch = patchFor(picker, id);
      if (picker === 'mightyStyle' && pendingViewMode) patch.agentViewMode = pendingViewMode;
      void (async () => {
        setSavingSetting(true);
        try {
          await client.updateSettings(sessionId, patch);
          closePicker();
          showToast('설정을 바꿨습니다', 'success');
          poll.refresh();
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          setSavingSetting(false);
        }
      })();
    },
    [client, closePicker, pendingViewMode, picker, poll, sessionId, settings],
  );

  const runHostCommand = useCallback(
    (action: MessageCommandAction, name: string) => {
      if (!client || !sessionId || commandRunning.current) return;
      commandRunning.current = true;
      void (async () => {
        try {
          const result = await client.runCommand(sessionId, action);
          if (result.message) setMessage({ title: `/${name}`, body: result.message });
          else showToast(`/${name} 실행됨`, 'success');
          poll.refresh();
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          commandRunning.current = false;
        }
      })();
    },
    [client, poll, sessionId],
  );

  const onCommand = useCallback(
    (command: MobileCommand) => {
      const action = commandActionOf(command);
      if (!action) return;
      if (action === 'rename') {
        setRenaming(true);
        return;
      }
      if (action === 'model') {
        openPicker('model');
        return;
      }
      if (action === 'permission') {
        openPicker('permissionMode');
        return;
      }
      if (isMessageAction(action)) runHostCommand(action, command.name);
    },
    [openPicker, runHostCommand],
  );

  const running = session?.status === 'running';

  return (
    <KeyboardAvoidingView
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 96 : 0}
      style={styles.screen}
    >
      <Stack.Screen
        options={{
          title: session?.title || '세션',
          headerRight: canPane
            ? () => (
                <Pressable
                  accessibilityLabel="실행 창 메뉴"
                  accessibilityRole="button"
                  hitSlop={8}
                  onPress={() => setMenuOpen(true)}
                >
                  <Text style={styles.headerAction}>⋯</Text>
                </Pressable>
              )
            : undefined,
        }}
      />

      {closed ? null : poll.needsRepair ? (
        <ErrorBanner message="재페어링 필요 — 저장된 키가 호스트와 일치하지 않습니다." />
      ) : poll.error ? (
        <ErrorBanner message={poll.error} />
      ) : null}

      <FlatList
        ref={listRef}
        data={entries}
        keyExtractor={(entry) => entry.id}
        renderItem={({ item }) => <LogEntryView entry={item} />}
        contentContainerStyle={styles.list}
        onScroll={onScroll}
        scrollEventThrottle={64}
        keyboardShouldPersistTaps="handled"
        maintainVisibleContentPosition={{ minIndexForVisible: 1 }}
        ListHeaderComponent={
          detail ? (
            <View>
              <SessionHeader
                detail={detail}
                settings={settings}
                showStatus={hasCapability(capabilities, 'status')}
                onEditSetting={openPicker}
              />
              {loadingOlder ? (
                <View style={styles.olderRow}>
                  <ActivityIndicator size="small" />
                  <Text style={styles.olderText}>이전 기록 불러오는 중…</Text>
                </View>
              ) : canLoadOlder ? (
                <Text style={styles.olderText}>위로 당기면 이전 기록을 더 불러옵니다.</Text>
              ) : null}
            </View>
          ) : null
        }
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

              <QueuedList
                items={detail.queued}
                manageable={canQueue}
                canRunNext={!running}
                busy={queueBusy}
                onRemove={removeQueued}
                onRunNext={runNext}
              />
            </View>
          ) : null
        }
      />

      <View style={{ paddingBottom: insets.bottom + spacing.sm }}>
        <Composer
          disabled={!client || session === undefined || sending}
          terminal={session?.terminal ?? false}
          running={running}
          sending={sending}
          submitModes={hasCapability(capabilities, 'submit-mode')}
          commands={canCommands ? commands : []}
          onSend={send}
          onStop={stop}
          onCommand={onCommand}
        />
      </View>

      <Sheet visible={menuOpen} onClose={() => setMenuOpen(false)}>
        <Text style={styles.menuTitle}>실행 창</Text>
        <Button
          label="이름 변경"
          tone="neutral"
          onPress={() => {
            setMenuOpen(false);
            setRenaming(true);
          }}
        />
        <Button
          label="창 닫기"
          tone="danger"
          onPress={() => {
            setMenuOpen(false);
            setClosing(true);
          }}
        />
        <Button label="취소" tone="ghost" onPress={() => setMenuOpen(false)} />
      </Sheet>

      <PromptDialog
        visible={renaming}
        title="이름 변경"
        description="1~80자까지 쓸 수 있습니다."
        initialValue={session?.title ?? ''}
        placeholder="실행 창 이름"
        confirmLabel="변경"
        busy={paneBusy}
        onConfirm={rename}
        onCancel={() => setRenaming(false)}
      />

      <ConfirmDialog
        visible={closing}
        title="창을 닫을까요?"
        description="실행 중이면 중지한 뒤 닫습니다. 되돌릴 수 없습니다."
        confirmLabel="닫기"
        destructive
        busy={paneBusy}
        onConfirm={closePane}
        onCancel={() => setClosing(false)}
      />

      <PickerSheet
        visible={picker !== undefined}
        title={picker ? settingTitles[picker] : ''}
        note={
          settings && !settings.editable
            ? '실행 중에는 설정을 바꿀 수 없습니다.'
            : pendingViewMode
              ? 'Mighty 보기와 함께 적용합니다.'
              : undefined
        }
        options={picker ? optionsFor(settings, picker) : []}
        selectedId={picker && settings ? valueFor(settings, picker) : undefined}
        busy={savingSetting}
        locked={settings ? !settings.editable : true}
        onSelect={applySetting}
        onClose={closePicker}
      />

      <MessageSheet
        visible={message !== undefined}
        title={message?.title ?? ''}
        message={message?.body ?? ''}
        onClose={() => setMessage(undefined)}
      />
    </KeyboardAvoidingView>
  );
}

const makeStyles = (palette: Palette) =>
  StyleSheet.create({
    screen: { backgroundColor: palette.background, flex: 1 },
    list: { padding: spacing.lg, paddingBottom: spacing.xl },
    footer: { gap: spacing.sm, paddingTop: spacing.sm },
    headerAction: { color: palette.text, fontSize: 22, paddingHorizontal: spacing.sm },
    menuTitle: { color: palette.text, fontSize: 16, fontWeight: '700' },
    olderRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    olderText: { color: palette.textFaint, fontSize: 12, paddingVertical: spacing.xs },
  });
