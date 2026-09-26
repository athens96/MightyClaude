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
import { describeRepairNeeded } from '@/api/relay/transport';
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
import { GuidedPanel } from '@/components/guided-panel';
import { MightyRunList } from '@/components/mighty-blocks';
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
import { Button, Chip, EmptyState, ErrorBanner } from '@/components/ui';
import { useAttachments } from '@/hooks/use-attachments';
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
import { defaultView, normalizeMighty } from '@/lib/mighty';
import { guidedRequestFor, panelOf } from '@/lib/styles';
import { sendWithAttachments, type SendRequest } from '@/lib/send';
import { useForgetRefusedSecret } from '@/store/hosts';
import { useHostClient, useLiveStore, useSessionCommands, useSessionDetail } from '@/store/live';
import { showToast } from '@/store/toast';
import { spacing, useStyles, usePalette, type Palette } from '@/theme';

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

/**
 * The lookup goes through `Object.hasOwn`: the key is the word the host sent, and a plain
 * object answers `constructor` with a function, which the toast would then try to draw.
 */
function acceptedMessage(accepted: string): string {
  return (Object.hasOwn(acceptedMessages, accepted) ? acceptedMessages[accepted] : undefined) ?? '전송';
}

const NO_ENTRIES: LogEntry[] = [];

/** Which body the screen shows: the transcript, or the Mighty block list. */
type BodyView = 'log' | 'blocks';

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
    case 'styleId':
      return { styleId: id };
  }
}

export default function SessionScreen() {
  const palette = usePalette();
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
  const [text, setText] = useState('');
  /** Set once the user picks a body themselves; until then the pane's own view decides. */
  const [chosenView, setChosenView] = useState<BodyView | undefined>(undefined);
  const [guidedAction, setGuidedAction] = useState<string | undefined>(undefined);
  const guidedRunning = useRef(false);
  /**
   * The group of the style map the user is looking at. It lives here rather than inside
   * the panel so an AskUserQuestion card — which takes the panel off screen while it is
   * answered — gives the user back the group they picked, not the host's.
   */
  const [guidedGroup, setGuidedGroup] = useState<string | undefined>(undefined);
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
  const canMighty = hasCapability(capabilities, 'mighty');
  const canAttach = hasCapability(capabilities, 'attachments');
  // "style": the host drives the pane from a style manifest and sends `panel`. Without
  // it the built-in two still arrive as their old payloads and are read through those.
  const canStyle = hasCapability(capabilities, 'style');

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

  // A refused secret is never presented again: it is dropped the moment the host says so.
  useForgetRefusedSecret(hostId, poll.needsRepair, poll.error);

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
    setChosenView(undefined);
    setText('');
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

  const onAttachmentError = useCallback((message: string) => showToast(message, 'error'), []);
  const attachments = useAttachments(onAttachmentError);
  const attachFiles = attachments.files;
  const clearAttachments = attachments.clear;
  const uploadAttachments = attachments.upload;
  const cancelAttachments = attachments.cancel;

  // Files belong to the pane they were picked for; leaving the screen — or switching
  // panes — also stops a run in flight, which cancels the uploads it had opened instead
  // of leaving them on the host for ten minutes.
  useEffect(() => {
    clearAttachments();
    return cancelAttachments;
  }, [cancelAttachments, clearAttachments, sessionId]);

  const send = useCallback(
    async (value: string, mode?: SubmitMode): Promise<boolean> => {
      if (!client || !sessionId) return false;
      setSending(true);
      try {
        // Uploads go first, and a `submit` that fails afterwards gives them back: the
        // host keeps 16 open uploads per pane, so leaking a few would make the next
        // attempt fail before it started.
        const request: SendRequest = { client, sessionId, text: value };
        if (mode) request.mode = mode;
        if (attachFiles.length > 0) {
          request.upload = () => uploadAttachments(client, sessionId);
        }
        const result = await sendWithAttachments(request);
        if (!result.ok) {
          showToast(result.error, 'error');
          return false;
        }
        showToast(acceptedMessage(result.accepted), 'success');
        clearAttachments();
        atBottom.current = true;
        poll.refresh();
        return true;
      } finally {
        setSending(false);
      }
    },
    [attachFiles.length, clearAttachments, client, poll, sessionId, uploadAttachments],
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
      const styleField: SettingField = canStyle ? 'styleId' : 'mightyStyle';
      if (picker === 'agentViewMode' && id === 'mighty' && optionsFor(settings, styleField).length > 1) {
        setPendingViewMode(id);
        setPicker(styleField);
        return;
      }
      const patch = patchFor(picker, id);
      if (picker === styleField && pendingViewMode) patch.agentViewMode = pendingViewMode;
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
    [canStyle, client, closePicker, pendingViewMode, picker, poll, sessionId, settings],
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

  // The host sends `mighty` only for a pane in Mighty view; an unknown shape is dropped
  // rather than trusted, so nothing here can be fed a field we cannot draw.
  const mighty = useMemo(
    () => (canMighty ? normalizeMighty(detail?.mighty) : undefined),
    [canMighty, detail?.mighty],
  );
  const panel = useMemo(() => panelOf(mighty, canStyle), [canStyle, mighty]);
  // A pane that switches style keeps this screen mounted, so the group the user was
  // looking at has to go with the style it belonged to.
  const panelStyleId = panel?.style.id;
  useEffect(() => {
    setGuidedGroup(undefined);
  }, [panelStyleId]);
  // An AskUserQuestion card is the one thing the pane is waiting on: the guided panel
  // steps aside for it rather than offering a second thing to press.
  const questionPending =
    detail?.permissions.some((permission) => permission.questionnaire !== undefined) ?? false;
  const view: BodyView = chosenView ?? defaultView(mighty);

  const runGuided = useCallback(
    (actionId: string) => {
      // `guidedAction` only disables the chips on the next render, so two taps inside one
      // frame would both fire; the ref is what actually holds the door.
      if (!client || !sessionId || !panel || guidedRunning.current) return;
      // Against a host without "style" the body has to go out in the old shape, and it is
      // the same capability that chose which panel is on screen (contract 7.5).
      const request = guidedRequestFor(panel, actionId, text, canStyle);
      guidedRunning.current = true;
      setGuidedAction(actionId);
      void (async () => {
        try {
          const result = await client.guided(sessionId, request);
          showToast(acceptedMessage(result.accepted), 'success');
          // Only text that actually went with the action leaves the composer.
          if (request.text) setText('');
          atBottom.current = true;
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          guidedRunning.current = false;
          setGuidedAction(undefined);
          // Either way: a 409 means the Mac changed the pane's style under us, so the
          // stale panel has to go rather than stay for the next tap to fail again.
          poll.refresh();
        }
      })();
    },
    [canStyle, client, panel, poll, sessionId, text],
  );

  const headerNode = detail ? (
    <View>
      <SessionHeader
        detail={detail}
        settings={settings}
        showStatus={hasCapability(capabilities, 'status')}
        styleAware={canStyle}
        onEditSetting={openPicker}
      />
      {mighty ? (
        <View style={styles.viewSwitch}>
          <Chip
            label="대화"
            color={palette.accent}
            selected={view === 'log'}
            onPress={() => setChosenView('log')}
          />
          <Chip
            label="블록"
            color={palette.accent}
            selected={view === 'blocks'}
            onPress={() => setChosenView('blocks')}
          />
        </View>
      ) : null}
      {view === 'log' && loadingOlder ? (
        <View style={styles.olderRow}>
          <ActivityIndicator size="small" />
          <Text style={styles.olderText}>이전 기록 불러오는 중…</Text>
        </View>
      ) : view === 'log' && canLoadOlder ? (
        <Text style={styles.olderText}>위로 당기면 이전 기록을 더 불러옵니다.</Text>
      ) : null}
    </View>
  ) : null;

  const footerNode = detail ? (
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
  ) : null;

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
        <ErrorBanner message={describeRepairNeeded(poll.error)} />
      ) : poll.error ? (
        <ErrorBanner message={poll.error} />
      ) : null}

      {view === 'blocks' && mighty ? (
        <MightyRunList
          runs={mighty.runs}
          contentContainerStyle={styles.list}
          header={headerNode}
          footer={footerNode}
        />
      ) : (
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
          ListHeaderComponent={headerNode}
          ListEmptyComponent={
            <EmptyState title={poll.loading ? '불러오는 중…' : '기록이 없습니다'} />
          }
          ListFooterComponent={footerNode}
        />
      )}

      <View style={{ paddingBottom: insets.bottom + spacing.sm }}>
        {!questionPending && panel ? (
          <GuidedPanel
            panel={panel}
            hasText={text.trim().length > 0}
            busyActionId={guidedAction}
            disabled={!client || sending}
            running={running}
            selectedGroupId={guidedGroup}
            onSelectGroup={setGuidedGroup}
            onRun={runGuided}
          />
        ) : null}
        <Composer
          text={text}
          onChangeText={setText}
          disabled={!client || session === undefined || sending}
          terminal={session?.terminal ?? false}
          running={running}
          sending={sending}
          submitModes={hasCapability(capabilities, 'submit-mode')}
          commands={canCommands ? commands : []}
          attachments={canAttach && session?.kind === 'claude' ? attachments : undefined}
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
    viewSwitch: { flexDirection: 'row', gap: spacing.xs, paddingVertical: spacing.xs },
  });
