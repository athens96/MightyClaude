import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  FlatList,
  Keyboard,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
  useWindowDimensions,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { Stack, router, useFocusEffect, useLocalSearchParams } from 'expo-router';
import { GestureDetector } from 'react-native-gesture-handler';
import { useHeaderHeight } from 'expo-router/react-navigation';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { describeError, isNotFound } from '@/api/client';
import { describeRepairNeeded, type RelayState } from '@/api/relay/transport';
import {
  ENTRY_PAGE_SIZE,
  type LogEntry,
  type MessageCommandAction,
  type MobileCommand,
  type MobilePermission,
  type MobileSessionDetail,
  type PlanDecisionKind,
  type QuestionAnswers,
  type SettingsPatch,
  type StructureItem,
  type SubmitMode,
} from '@/api/types';
import { Composer } from '@/components/composer';
import { LogEntryView } from '@/components/log-entry-view';
import { GuidedPanel } from '@/components/guided-panel';
import { MightyRunList } from '@/components/mighty-blocks';
import { PendingRequestView } from '@/components/pending-request';
import { MightyStructureView } from '@/components/structure-view';
import { NextActionChips } from '@/components/next-action-chips';
import { AntigravityPermissionCard } from '@/components/antigravity-permission-card';
import { PermissionCard } from '@/components/permission-card';
import { PlanCard } from '@/components/plan-card';
import { QuestionnaireCard } from '@/components/questionnaire-card';
import { QueuedList } from '@/components/queued-list';
import {
  SessionHeader,
  SessionTitle,
  optionsFor,
  settingFields,
  settingTitle,
  valueFor,
  type SettingField,
} from '@/components/session-header';
import { SessionPanel } from '@/components/session-panel';
import { ConfirmDialog, MessageSheet, PickerSheet, PromptDialog, Sheet } from '@/components/sheets';
import { Button, EmptyState, ErrorBanner, SegmentedControl } from '@/components/ui';
import { useAttachments } from '@/hooks/use-attachments';
import { useCapabilities, useRunsAntigravity } from '@/hooks/use-capabilities';
import { useFollowBottom } from '@/hooks/use-follow-bottom';
import {
  antigravityActions,
  antigravityAnsweredToast,
  sendAntigravityAnswer,
  type AntigravityDecision,
} from '@/lib/antigravity-permission';
import { useLongPoll } from '@/hooks/use-long-poll';
import { usePendingAction } from '@/hooks/use-pending';
import { usePanelExpanded } from '@/hooks/use-panel-expanded';
import { hasCapability } from '@/lib/capabilities';
import { commandActionOf, isMessageAction } from '@/lib/commands';
import {
  combineEntries,
  isHistoryExhausted,
  oldestEntryId,
  prependOlderPage,
  retainDropped,
} from '@/lib/history';
import { t } from '@/lib/i18n';
import { pushViewing } from '@/lib/push/runtime';
import { answeredToast, isPlanRequest, pendingPlan, planAnswerBody } from '@/lib/plan';
import { PREPEND_HOLD_MS, blocksProgressKey, entriesProgressKey, keyboardEvents } from '@/lib/follow';
import { defaultView, normalizeMighty } from '@/lib/mighty';
import {
  receiveStructure,
  structureQuery,
  toggleCardFold,
  type HeldStructure,
  type StructureState,
} from '@/lib/structure';
import { fillDraft, latestNextActions } from '@/lib/next-actions';
import { composerRunning, stopVerdict, type StopVerdict } from '@/lib/resync';
import { guidedRequestFor, panelOf } from '@/lib/styles';
import {
  applyAnswer,
  beforeSend,
  draftToRestore,
  drawsStandIn,
  dropRequest,
  failRequest,
  heldInLog,
  heldRuns,
  newClientId,
  newPendingRequest,
  nextDeadline,
  settle as settleRequests,
  shownInBlocks,
  shownInLog,
  shownInStructure,
  structureWithout,
  type PaneSnapshot,
  type PendingRequest,
} from '@/lib/pending-requests';
import type { PendingState } from '@/lib/pending';
import { structureBoards } from '@/lib/pipeline-board';
import { sendWithAttachments, type SendRequest } from '@/lib/send';
import { submitScroll, submitViewFor } from '@/lib/submit-scroll';
import { panelContent, panelShownExpanded, settingsSummary, toggledPanel } from '@/lib/session-panel';
import { useForgetRefusedSecret } from '@/store/hosts';
import {
  useDetailReceivedAt,
  useHostClient,
  useLiveStore,
  useSessionCommands,
  useSessionDetail,
} from '@/store/live';
import { showToast } from '@/store/toast';
import { spacing, typeScale, useStyles, usePalette, type Palette } from '@/theme';

/**
 * The host reports what actually happened, not what was asked for: "next request" on a
 * pane that has just gone idle is answered with `started`. The toast therefore follows the
 * answer and never the button; an unknown word falls back to plain "sent". The values are
 * locale keys, read when the toast is shown.
 */
const acceptedMessageKeys: Record<string, string> = {
  started: 'phone.session.accepted.started',
  steered: 'phone.session.accepted.steered',
  queued: 'phone.session.accepted.queued',
};

/**
 * The lookup goes through `Object.hasOwn`: the key is the word the host sent, and a plain
 * object answers `constructor` with a function, which the toast would then try to draw.
 */
function acceptedMessage(accepted: string): string {
  const key = Object.hasOwn(acceptedMessageKeys, accepted) ? acceptedMessageKeys[accepted] : undefined;
  return t(key ?? 'phone.session.accepted.started');
}

const NO_ENTRIES: LogEntry[] = [];

/** Index 1 is the first row: index 0 is the list header. */
const HOLD_FIRST_ROW = { minIndexForVisible: 1 };

/** A permission request is only unique within its run. */
function requestKey(request: Pick<MobilePermission, 'id' | 'runId'>): string {
  return `${request.runId}:${request.id}`;
}

/** Which body the screen shows: the transcript, the Mighty block list, or its structure. */
type BodyView = 'log' | 'blocks' | 'structure';

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
  const receivedAt = useDetailReceivedAt(hostId, sessionId);
  const applyDetail = useLiveStore((store) => store.applyDetail);
  const setCommands = useLiveStore((store) => store.setCommands);
  const commands = useSessionCommands(hostId, sessionId);
  const capabilities = useCapabilities(hostId, client);
  const antigravity = useRunsAntigravity(hostId);
  const insets = useSafeAreaInsets();
  const headerHeight = useHeaderHeight();
  const { height: windowHeight } = useWindowDimensions();
  const [keyboardShown, setKeyboardShown] = useState(false);
  const [panelExpanded, setPanelExpanded] = usePanelExpanded();
  /** The question (`requestKey`) the panel was opened for while it was docked above it. */
  const [panelPeekFor, setPanelPeekFor] = useState<string | undefined>(undefined);
  /** Clears a keyboard flag no hide event came for after the panel lowered the keyboard. */
  const keyboardCheck = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(keyboardCheck.current), []);
  const [sending, setSending] = useState(false);
  const [deciding, setDeciding] = useState(false);
  /**
   * `deciding` only disables the cards on the next render, and it is back to false before
   * the refresh drops the request, so the ref is what holds the door against a second tap.
   */
  const decidingRef = useRef(false);
  /**
   * Requests (`requestKey`) answered or cancelled here that the detail still lists. Their
   * card stays off screen until the host drops them, so it cannot be sent a second time.
   */
  const [settledRequests, setSettledRequests] = useState<ReadonlySet<string>>(() => new Set());
  /** The host's reason (`requestKey` → text) for refusing an Antigravity card's last answer. */
  const [antigravityErrors, setAntigravityErrors] = useState<ReadonlyMap<string, string>>(() => new Map());
  const [paneBusy, setPaneBusy] = useState(false);
  const [queueBusy, setQueueBusy] = useState(false);
  const [savingSetting, setSavingSetting] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [closing, setClosing] = useState(false);
  const [picker, setPicker] = useState<SettingField | undefined>(undefined);
  const [text, setText] = useState('');
  const textRef = useRef(text);
  textRef.current = text;
  /**
   * The send whose words a failure may give back, and whether the box was touched since
   * (natural submit: only an empty, untouched composer gets the draft back).
   */
  const sentDraft = useRef<{ clientId: string; edited: boolean } | undefined>(undefined);
  const changeText = useCallback((value: string) => {
    if (sentDraft.current) sentDraft.current.edited = true;
    setText(value);
  }, []);
  /** A send on its way; a second one waits (the round buttons are locked meanwhile too). */
  const sendingRef = useRef(false);
  /**
   * Sent requests the host has not shown yet (`lib/pending-requests.ts`). The ref is the
   * truth, so a change and what it means (a request landed) are read in one place.
   */
  const [pendingRequests, setPendingRequests] = useState<readonly PendingRequest[]>([]);
  const pendingRef = useRef<readonly PendingRequest[]>([]);
  /** Bumped when the views move to an own request: at send, at the answer, and when it lands. */
  const [ownMove, setOwnMove] = useState(0);
  /** Bumped when a send was queued: the queue list flashes (B3). */
  const [queueFlash, setQueueFlash] = useState(0);
  /** Re-runs the reconcile when a grace ends on a pane the poll has gone quiet on. */
  const [settleTick, setSettleTick] = useState(0);
  const updatePending = useCallback(
    (change: (previous: readonly PendingRequest[]) => readonly PendingRequest[]) => {
      const previous = pendingRef.current;
      const next = change(previous);
      if (next === previous) return;
      pendingRef.current = next;
      setPendingRequests(next);
    },
    [],
  );
  const stopAction = usePendingAction();
  const runStop = stopAction.run;
  const resetStop = stopAction.reset;
  /** The pane on screen: an answer that comes back for another one changes nothing here. */
  const shownSession = useRef(sessionId);
  shownSession.current = sessionId;
  /** A slash command the phone runs, on its way: the composer shows the pending mark. */
  const [commandRun, setCommandRun] = useState<{ name: string; state: PendingState } | undefined>(undefined);
  const composerInput = useRef<TextInput>(null);
  /** Set once the user picks a body themselves; until then the pane's own view decides. */
  const [chosenView, setChosenView] = useState<BodyView | undefined>(undefined);
  /**
   * The structure view the Mac last sent and its tag. It lives apart from the detail: the
   * Mac sends it only while the 구조 view asks, and only when it changed, so a detail
   * without it must not take it away.
   */
  const [heldStructure, setHeldStructure] = useState<HeldStructure | undefined>(undefined);
  /** Finished requests the user opened in the 구조 view, by run id: the rest show folded. */
  const [unfoldedCards, setUnfoldedCards] = useState<ReadonlySet<string>>(() => new Set());
  const toggleCardFolded = useCallback(
    (item: StructureItem) => setUnfoldedCards((previous) => toggleCardFold(item, previous)),
    [],
  );
  const heldStructureRef = useRef<HeldStructure | undefined>(undefined);
  /** Whether the poll asks for the structure: only while the 구조 view is on screen. */
  const asksStructure = useRef(false);
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
  /** The detail revision the host has since said was not running (see `composerRunning`). */
  const [settledRevision, setSettledRevision] = useState<number | undefined>(undefined);

  // History paged in with `entries?before=`; kept apart from the long-poll window so an
  // entry arriving while a page loads can neither duplicate nor reorder what is shown.
  const [older, setOlder] = useState<LogEntry[]>([]);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const loadingOlderRef = useRef(false);
  const [exhausted, setExhausted] = useState(false);
  /**
   * The transcript holds the rows on screen in place while older ones go in above
   * (`PREPEND_HOLD_MS`) and whenever the reader is further up (`detached`); held while it
   * follows, it would fight every jump to the newest.
   */
  const [holdingPosition, setHoldingPosition] = useState(false);
  const holdTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(holdTimer.current), []);

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
  const canPush = hasCapability(capabilities, 'push');

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
    showToast(t('phone.session.toast.paneGone'));
    leavePane();
  }, [leavePane]);

  const fetchPage = useCallback(
    async (since: number | undefined, signal: AbortSignal) => {
      if (!client || !sessionId) throw new Error(t('phone.session.notFound'));
      try {
        const asked = asksStructure.current;
        const data = await client.session(sessionId, {
          since,
          wait: since === undefined ? 0 : 10,
          ...(asked ? { structure: structureQuery(heldStructureRef.current) } : {}),
          signal,
        });
        if (asked) {
          const next = receiveStructure(heldStructureRef.current, (data as { mighty?: unknown }).mighty);
          if (next !== heldStructureRef.current) {
            heldStructureRef.current = next;
            setHeldStructure(next);
          }
        }
        return data;
      } catch (error) {
        // A pane closed on the Mac answers 404 forever; leave instead of backing off.
        if (isNotFound(error)) paneGone();
        throw error;
      }
    },
    [client, paneGone, sessionId],
  );

  const onData = useCallback(
    (data: MobileSessionDetail, fresh: boolean) => {
      if (hostId && sessionId) applyDetail(hostId, sessionId, data, fresh);
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

  const watchLink = useCallback(
    (listener: (state: RelayState) => void) => client?.onStateChange(listener) ?? (() => undefined),
    [client],
  );

  const poll = useLongPoll<MobileSessionDetail>({
    enabled: Boolean(client && sessionId) && !closed,
    fetchPage,
    revisionOf: (data) => data.revision,
    onData,
    subscribe,
    watchLink,
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

  useEffect(() => {
    setOlder([]);
    setExhausted(false);
    previousLive.current = NO_ENTRIES;
    closedRef.current = false;
    setClosed(false);
    setSettledRevision(undefined);
    setChosenView(undefined);
    heldStructureRef.current = undefined;
    setHeldStructure(undefined);
    setText('');
    sentDraft.current = undefined;
    pendingRef.current = [];
    setPendingRequests([]);
    setCommandRun(undefined);
    resetStop();
  }, [resetStop, sessionId]);

  // The host sends only the newest entries and replaces that window wholesale, so what
  // slides off its front is caught here: no `before=` page could ever bring it back.
  useEffect(() => {
    const previous = previousLive.current;
    previousLive.current = live;
    if (previous.length === 0 || previous === live) return;
    setOlder((prev) => retainDropped(prev, previous, live));
  }, [live]);

  // The composer drops its home-indicator inset while the keyboard is up, since the
  // keyboard already covers that strip. iOS says so before the keyboard moves, so the
  // inset changes with it in one step; Android reports only the "did" events. The list
  // shrinking under it is a layout change the follow hook answers by pinning again.
  useEffect(() => {
    const events = keyboardEvents(Platform.OS);
    const shown = Keyboard.addListener(events.show, () => setKeyboardShown(true));
    const hidden = Keyboard.addListener(events.hide, () => setKeyboardShown(false));
    return () => {
      shown.remove();
      hidden.remove();
    };
  }, []);

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

  // The host does not push about the session the phone is showing (docs/mobile-remote.md "푸시 알림").
  useFocusEffect(
    useCallback(() => {
      if (!hostId || !sessionId || !canPush) return undefined;
      pushViewing.enter(hostId, sessionId);
      return () => pushViewing.leave();
    }, [canPush, hostId, sessionId]),
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
    clearTimeout(holdTimer.current);
    setHoldingPosition(true);
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
      // The page's rows mount over the next few frames; the hold outlasts them.
      holdTimer.current = setTimeout(() => setHoldingPosition(false), PREPEND_HOLD_MS);
    }
  }, [canLoadOlder, client, older, sessionId]);

  const onScroll = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      if (event.nativeEvent.contentOffset.y < 120) void loadOlder();
    },
    [loadOlder],
  );

  // Pulling up past the newest content refetches the session. The note under the list
  // stays up until the fetch has settled, and at least briefly so a fast one is seen.
  const [refreshing, setRefreshing] = useState(false);
  const refreshPoll = poll.refresh;
  const pullRefresh = useCallback(() => {
    setRefreshing(true);
    refreshPoll();
  }, [refreshPoll]);
  useEffect(() => {
    if (!refreshing || poll.loading) return undefined;
    const timer = setTimeout(() => setRefreshing(false), 600);
    return () => clearTimeout(timer);
  }, [poll.loading, refreshing]);

  // Both bodies stay on their newest content while it grows, until the user scrolls up
  // to read. Only one is mounted at a time; sending pins whichever it is.
  const logFollow = useFollowBottom(onScroll, pullRefresh);
  const blockFollow = useFollowBottom(undefined, pullRefresh);
  // The structure follows its newest content only while the user is at the bottom: new
  // work never pulls it back from further up (no `followProgress` below).
  const structureFollow = useFollowBottom(undefined, pullRefresh);
  const followLog = logFollow.follow;
  const followBlocks = blockFollow.follow;
  const followStructure = structureFollow.follow;
  const followNewest = useCallback(() => {
    followLog();
    followBlocks();
    followStructure();
  }, [followBlocks, followLog, followStructure]);

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

  // What the pane holds now, read by the send that starts a request and by the reconcile.
  const snapshotRef = useRef<PaneSnapshot>({ entries: NO_ENTRIES });
  const runningRef = useRef(false);

  /**
   * Natural submit on the phone (B1): the composer empties at once and, unless the pane is
   * busy (it queues) or the words are a slash command, the shown view draws the request
   * with 전송 중 and moves to it. The host's answer then either keeps it until its entry
   * lands (started), only watches it by its words (queued: the queue list flashes, and the
   * views move to it when it runs), drops it (steered), or turns it failed with the message
   * in place, and the draft comes back if the box is still untouched.
   */
  const send = useCallback(
    (value: string, mode?: SubmitMode) => {
      if (!client || !sessionId || sendingRef.current) return;
      sendingRef.current = true;
      const clientId = newClientId();
      const at = Date.now();
      const drawn = drawsStandIn(value, { running: runningRef.current, ...(mode ? { mode } : {}) });
      setText('');
      sentDraft.current = { clientId, edited: false };
      // A request with no stand-in is still watched, so the views move to it when it lands.
      updatePending((previous) => [...beforeSend(previous), newPendingRequest(snapshotRef.current, clientId, value, at, drawn)]);
      if (drawn) setOwnMove((count) => count + 1);
      setSending(true);
      void (async () => {
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
          // The user went to another pane meanwhile: nothing of this answer belongs there.
          if (shownSession.current !== sessionId) return;
          const fail = (message: string, cancelled: boolean) => {
            if (drawn && !cancelled) updatePending((previous) => failRequest(previous, clientId, message, Date.now()));
            else updatePending((previous) => dropRequest(previous, clientId));
            // A failure shows once: in its block when one shows it, else as a toast.
            const inBlock = pendingRef.current.some(
              (pending) => pending.clientId === clientId && pending.drawn && pending.phase === 'failed',
            );
            if (!inBlock) showToast(message, 'error');
            const draft = sentDraft.current;
            if (draft?.clientId === clientId) {
              const back = draftToRestore(value, textRef.current, draft.edited);
              if (back !== undefined) setText(back);
            }
          };
          if (!result.ok) {
            fail(result.error, result.cancelled);
            return;
          }
          const answer = result.answer;
          // A word the shared rule does not accept: the host did not take it.
          if (!answer.accepted) {
            fail(t('remote.error.startFailed'), false);
            return;
          }
          updatePending((previous) => applyAnswer(previous, clientId, answer, Date.now()));
          // The poll may already have brought its entry or board: reconcile now, not at the next one.
          setSettleTick((count) => count + 1);
          if (answer.outcome === 'queued') setQueueFlash((count) => count + 1);
          // The bubble says a started request was sent; anything else gets the host's word.
          if (!(drawn && answer.outcome === 'started')) showToast(acceptedMessage(result.accepted), 'success');
          // A queued request is no move yet: it is own when it runs (its entry lands, the settle below).
          if (answer.outcome === 'started' || answer.outcome === 'steered') setOwnMove((count) => count + 1);
          clearAttachments();
          poll.refresh();
        } finally {
          sendingRef.current = false;
          setSending(false);
        }
      })();
    },
    [attachFiles.length, clearAttachments, client, poll, sessionId, updatePending, uploadAttachments],
  );

  // Stop can be pressed on a picture of the pane that is out of date — the run ended
  // while the phone was away. The pane is read again whatever the answer, and "nothing
  // was running" retires the picture the button was drawn from straight away.
  const detailRevision = detail?.revision;
  const stop = useCallback(() => {
    if (!client || !sessionId) return;
    const drawnFrom = detailRevision;
    // The stop button locks and shows the pending mark while the call is out (A3); a second
    // tap meanwhile does nothing. Its outcome is the toast, as before.
    void runStop(async () => {
      let verdict: StopVerdict;
      try {
        verdict = stopVerdict(await client.stop(sessionId));
      } catch (error) {
        verdict = stopVerdict({ failure: describeError(error) });
      }
      if (shownSession.current !== sessionId) return { ok: true };
      if (verdict.kind === 'requested') showToast(t('phone.session.toast.stopRequested'));
      else if (verdict.kind === 'notRunning') {
        setSettledRevision(drawnFrom);
        showToast(t('phone.session.stopNotRunning'));
      } else showToast(verdict.message, 'error');
      poll.refresh();
      return { ok: true };
    });
  }, [client, detailRevision, poll, runStop, sessionId]);

  const settle = useCallback((key: string) => {
    setSettledRequests((prev) => new Set(prev).add(key));
  }, []);

  /** `done` replaces the allow/deny toast, e.g. for a questionnaire's cancel. */
  const decide = useCallback(
    async (requestId: string, runId: string, allow: boolean, done?: string) => {
      if (!client || !sessionId || decidingRef.current) return;
      decidingRef.current = true;
      setDeciding(true);
      try {
        await client.respondPermission(sessionId, { requestId, runId, allow });
        settle(requestKey({ id: requestId, runId }));
        showToast(done ?? (allow ? t('phone.session.toast.allowed') : t('phone.session.toast.denied')), 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        decidingRef.current = false;
        setDeciding(false);
      }
    },
    [client, poll, sessionId, settle],
  );

  /**
   * One of an Antigravity card's three answers. The card leaves when the host drops it; a
   * refusal (409) keeps it on screen with the host's reason, as on the Mac.
   */
  const answerAntigravity = useCallback(
    async (permission: MobilePermission, decision: AntigravityDecision) => {
      if (!client || !sessionId || decidingRef.current) return;
      decidingRef.current = true;
      setDeciding(true);
      const key = requestKey(permission);
      try {
        const result = await sendAntigravityAnswer(client, sessionId, permission, decision);
        setAntigravityErrors((prev) => {
          if (result.ok && !prev.has(key)) return prev;
          const next = new Map(prev);
          if (result.ok) next.delete(key);
          else next.set(key, result.reason);
          return next;
        });
        if (result.ok) {
          settle(key);
          showToast(antigravityAnsweredToast(decision), 'success');
        }
        poll.refresh();
      } finally {
        decidingRef.current = false;
        setDeciding(false);
      }
    },
    [client, poll, sessionId, settle],
  );

  /** One of the plan card's four answers; the plan leaves the screen once the host took it. */
  const answerPlan = useCallback(
    async (requestId: string, runId: string, decision: PlanDecisionKind, feedback?: string) => {
      if (!client || !sessionId || decidingRef.current) return;
      decidingRef.current = true;
      setDeciding(true);
      try {
        await client.answerPlan(sessionId, planAnswerBody({ id: requestId, runId }, decision, feedback));
        settle(requestKey({ id: requestId, runId }));
        showToast(answeredToast(decision), 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        decidingRef.current = false;
        setDeciding(false);
      }
    },
    [client, poll, sessionId, settle],
  );

  const answer = useCallback(
    async (requestId: string, runId: string, answers: QuestionAnswers) => {
      if (!client || !sessionId || decidingRef.current) return;
      decidingRef.current = true;
      setDeciding(true);
      try {
        await client.answer(sessionId, { requestId, runId, answers });
        settle(requestKey({ id: requestId, runId }));
        showToast(t('phone.session.toast.answered'), 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        decidingRef.current = false;
        setDeciding(false);
      }
    },
    [client, poll, sessionId, settle],
  );

  const removeQueued = useCallback(
    (itemId: string) => {
      if (!client || !sessionId) return;
      void (async () => {
        setQueueBusy(true);
        try {
          await client.removeQueued(sessionId, itemId);
          showToast(t('phone.session.toast.dequeued'), 'success');
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

  const firstQueued = detail?.queued[0]?.text;
  const runNext = useCallback(() => {
    if (!client || !sessionId) return;
    // The baseline is the pane before the call: its entry may come with a poll while the
    // call is still out.
    const asked = Date.now();
    const watched = firstQueued === undefined ? undefined : newPendingRequest(snapshotRef.current, newClientId(asked), firstQueued, asked, false);
    void (async () => {
      setQueueBusy(true);
      try {
        await client.runNext(sessionId);
        if (shownSession.current !== sessionId) return;
        showToast(t('phone.session.toast.nextStarted'), 'success');
        // 다음 실행 is an own request (B2): nothing is drawn for it, but the views move to
        // its entry when it lands. Started with no ids: any new request entry is it.
        if (watched) {
          updatePending((previous) => [...previous, { ...watched, phase: 'started', answeredAt: Date.now() }]);
          setSettleTick((count) => count + 1);
        }
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setQueueBusy(false);
      }
    })();
  }, [client, firstQueued, poll, sessionId, updatePending]);

  const rename = useCallback(
    (title: string) => {
      if (!client || !sessionId) return;
      void (async () => {
        setPaneBusy(true);
        try {
          await client.rename(sessionId, title);
          setRenaming(false);
          showToast(t('phone.session.toast.renamed'), 'success');
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

  const setAutoTitle = useCallback(() => {
    if (!client || !sessionId) return;
    void (async () => {
      setPaneBusy(true);
      try {
        await client.setAutoTitle(sessionId);
        setRenaming(false);
        showToast(t('pane.rename.automatic'), 'success');
        poll.refresh();
      } catch (error) {
        showToast(describeError(error), 'error');
      } finally {
        setPaneBusy(false);
      }
    })();
  }, [client, poll, sessionId]);

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
        showToast(t('phone.session.toast.closed'), 'success');
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
        showToast(t('phone.session.noSettings'), 'error');
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
          showToast(t('phone.session.toast.settingsChanged'), 'success');
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
      const since = Date.now();
      setCommandRun({ name, state: { kind: 'pending', since } });
      void (async () => {
        try {
          const result = await client.runCommand(sessionId, action);
          if (shownSession.current !== sessionId) return;
          if (result.message) setMessage({ title: `/${name}`, body: result.message });
          else showToast(t('phone.session.toast.commandRan', { name }), 'success');
          poll.refresh();
        } catch (error) {
          showToast(describeError(error), 'error');
        } finally {
          commandRunning.current = false;
          // The message or toast says how it went; the mark only ends by the pending rule.
          if (shownSession.current === sessionId) setCommandRun({ name, state: { kind: 'done', since, at: Date.now() } });
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

  const running = composerRunning(session?.status, detail?.revision, settledRevision);
  runningRef.current = running;
  // The override covers one stale detail only. Once another revision arrives it has
  // spoken for itself, and a later detail that happens to reuse the number (the Mac
  // restarted and counts again) must not hide Stop for a run that is really going.
  useEffect(() => {
    if (settledRevision !== undefined && detail?.revision !== undefined && detail.revision !== settledRevision) {
      setSettledRevision(undefined);
    }
  }, [detail?.revision, settledRevision]);

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
  // An AskUserQuestion card is the one thing the pane is waiting on: it is docked right
  // above the composer, as on the Mac, and the guided panel steps aside for it rather
  // than offering a second thing to press. Ordinary permission cards stay in the list.
  const questionRequests = useMemo(
    () =>
      detail?.permissions.filter(
        (permission) => permission.questionnaire !== undefined && !settledRequests.has(requestKey(permission)),
      ) ?? [],
    [detail?.permissions, settledRequests],
  );
  // Once the host no longer lists a settled request its key has done its job.
  useEffect(() => {
    const listed = new Set(detail?.permissions.map(requestKey));
    setSettledRequests((prev) => {
      const kept = [...prev].filter((key) => listed.has(key));
      return kept.length === prev.size ? prev : new Set(kept);
    });
    setAntigravityErrors((prev) => {
      const kept = [...prev].filter(([key]) => listed.has(key));
      return kept.length === prev.size ? prev : new Map(kept);
    });
  }, [detail?.permissions]);
  const questionRequest = questionRequests[0];
  const questionPending = questionRequest !== undefined;
  // Claude's plan (ExitPlanMode) docks the same way, with its own four answers.
  const planRequest = useMemo(
    () => pendingPlan(detail?.permissions.filter((permission) => !settledRequests.has(requestKey(permission))) ?? []),
    [detail?.permissions, settledRequests],
  );
  const planPending = planRequest !== undefined;
  // With the keyboard up (typing a "custom answer") the body gets less room, so the
  // transcript above keeps a strip of its own.
  const questionBodyHeight = Math.round(Math.min(320, windowHeight * (keyboardShown ? 0.22 : 0.4)));
  const view: BodyView = chosenView ?? defaultView(mighty);
  const blocksShown = view === 'blocks' && Boolean(mighty);
  const structureShown = view === 'structure' && Boolean(mighty);
  // The Mac sends the structure only to a phone that asks, and only a Mac that lists
  // "structure" knows the question; an older one answers 400 to it.
  const canStructure = hasCapability(capabilities, 'structure');
  asksStructure.current = structureShown && canStructure;
  const refreshPollRef = useRef(poll.refresh);
  refreshPollRef.current = poll.refresh;
  // Opening the 구조 view asks at once rather than waiting out the long poll.
  useEffect(() => {
    if (structureShown && canStructure) refreshPollRef.current();
  }, [canStructure, structureShown]);
  const structureState: StructureState =
    capabilities === undefined
      ? { kind: 'loading' }
      : !canStructure
        ? { kind: 'unsupported' }
        : (heldStructure?.state ?? { kind: 'loading' });
  const pull = blocksShown ? blockFollow.pull : structureShown ? structureFollow.pull : logFollow.pull;

  // ---- natural submit: reconcile the stand-ins with what the poll brought, and move to them.
  const queuedTexts = detail?.queued;
  const snapshot = useMemo<PaneSnapshot>(
    () => ({
      entries,
      ...(mighty ? { runs: mighty.runs } : {}),
      ...(queuedTexts ? { queued: queuedTexts.map((item) => item.text) } : {}),
    }),
    [entries, mighty, queuedTexts],
  );
  snapshotRef.current = snapshot;
  /** The run ids the 파이프라인's boards hold; undefined while it has none to show. */
  const boardRuns = useMemo(
    () =>
      structureState.kind === 'ok'
        ? new Set(structureBoards(structureState.structure).flatMap((board) => board.items.map((item) => item.runId)))
        : undefined,
    [structureState],
  );
  // Each view keeps its stand-in until its own data has the request: the transcript and 흐름
  // get it in the same answer as the entry; the 파이프라인 waits for a board with its run.
  const holdsRef = useRef<(request: PendingRequest) => boolean>(() => true);
  holdsRef.current = (request) => {
    if (!structureShown) return true;
    if (structureState.kind === 'unsupported' || structureState.kind === 'unreadable') return true;
    return boardRuns !== undefined && (request.arrivedRunIds ?? []).some((id) => boardRuns.has(id));
  };
  useEffect(() => {
    let landedNow = false;
    updatePending((previous) => {
      if (previous.length === 0) return previous;
      const result = settleRequests(previous, snapshot, Date.now(), holdsRef.current);
      landedNow = result.landed;
      return result.requests;
    });
    if (landedNow) setOwnMove((count) => count + 1);
  }, [boardRuns, settleTick, snapshot, structureShown, updatePending]);
  // A grace that ends while the poll is quiet settles on its own time, not the next answer.
  useEffect(() => {
    const at = nextDeadline(pendingRequests);
    if (at === null) return undefined;
    const timer = setTimeout(() => setSettleTick((count) => count + 1), Math.max(0, at - Date.now()));
    return () => clearTimeout(timer);
  }, [pendingRequests]);
  // An own request moves the shown view to it, wherever it was (pending.json scrollTargets):
  // at send, onto the stand-in at once, and again when the host's entry lands, since the
  // first move ran before the entry existed. The 파이프라인 also goes back to its newest
  // board (`ownSend` on MightyStructureView).
  const followNewestRef = useRef(followNewest);
  followNewestRef.current = followNewest;
  useEffect(() => {
    if (ownMove === 0) return;
    const decision = submitScroll(submitViewFor(blocksShown ? 'blocks' : structureShown ? 'structure' : 'log'), true, true, false);
    if (decision.scroll === 'toRequest') followNewestRef.current();
    // Only a new own move counts: the view it applies to is read at that moment.
  }, [ownMove]);
  // A stand-in whose 전송 중 has shown stays its shortest time in place of the host's own
  // row, entry or board (pending.json displayRule), which is held back meanwhile.
  const blockStandIn = blocksShown && mighty ? shownInBlocks(pendingRequests, mighty.runs) : undefined;
  const structureStandIn = structureShown ? shownInStructure(pendingRequests, boardRuns) : undefined;
  const logStandIns = useMemo(() => heldInLog(pendingRequests), [pendingRequests]);
  // Keyed by the held ids, which change only with the request that holds.
  const blockHeld = heldRuns(blockStandIn).join('\n');
  const shownRuns = useMemo(() => {
    const held = blockHeld ? blockHeld.split('\n') : [];
    return mighty && held.length > 0 ? mighty.runs.filter((run) => !held.includes(run.id)) : (mighty?.runs ?? []);
  }, [mighty, blockHeld]);
  const structureHeld = heldRuns(structureStandIn).join('\n');
  const shownStructureState = useMemo<StructureState>(
    () =>
      structureState.kind === 'ok' && structureHeld
        ? { kind: 'ok', structure: structureWithout(structureState.structure, structureHeld.split('\n')) }
        : structureState,
    [structureState, structureHeld],
  );
  const pendingNode = (() => {
    if (blocksShown && mighty) {
      const request = blockStandIn;
      return request ? <PendingRequestView key={request.clientId} request={request} look="card" /> : null;
    }
    if (structureShown) {
      const request = structureStandIn;
      return request ? <PendingRequestView key={request.clientId} request={request} look="card" /> : null;
    }
    const requests = shownInLog(pendingRequests, entries);
    return requests.length === 0
      ? null
      : requests.map((request) => <PendingRequestView key={request.clientId} request={request} look="bubble" />);
  })();

  // New work pulls the shown list back to its newest content, even from further up,
  // unless the user is dragging it.
  const blocksProgress = useMemo(() => blocksProgressKey(mighty?.runs ?? []), [mighty]);
  const entriesProgress = useMemo(() => entriesProgressKey(entries), [entries]);
  const followBlockProgress = blockFollow.followProgress;
  const followLogProgress = logFollow.followProgress;
  useEffect(() => {
    if (blocksShown) followBlockProgress();
  }, [blocksProgress, blocksShown, followBlockProgress]);
  useEffect(() => {
    if (!blocksShown && !structureShown) followLogProgress();
  }, [entriesProgress, blocksShown, structureShown, followLogProgress]);

  // The `next:` options of the last reply, once its turn is over and until a user
  // message follows it: under that reply in the log, above the composer in the blocks
  // view. A tap fills the composer and nothing is sent; a draft already there is kept
  // and the option goes on a new line after it. A local terminal pane has no box to
  // fill, so it gets none.
  const terminalPane = session?.terminal ?? false;
  const nextActions = useMemo(
    () => (running || terminalPane ? undefined : latestNextActions(entries)),
    [entries, running, terminalPane],
  );
  // The rows redraw when the next actions move or the host's name for `gemini` arrives.
  const entriesExtra = useMemo(() => ({ nextActions, antigravity, logStandIns }), [nextActions, antigravity, logStandIns]);
  /** A fresh object per fill, so filling the same text twice still moves the caret. */
  const [filled, setFilled] = useState<{ end: number } | undefined>(undefined);
  const fillComposer = useCallback(
    (value: string) => {
      const next = fillDraft(text, value);
      changeText(next);
      setFilled({ end: next.length });
    },
    [changeText, text],
  );
  // Once the filled text is on screen: focus and put the caret after it, one time only,
  // so the box stays uncontrolled for selection and typing behaves normally.
  useEffect(() => {
    if (!filled) return;
    composerInput.current?.focus();
    composerInput.current?.setSelection(filled.end, filled.end);
  }, [filled]);

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
          followNewest();
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
    [canStyle, client, followNewest, panel, poll, sessionId, text],
  );

  // The settings and the status line live in the panel above the composer, the log/blocks
  // switch in its row: at the top of the transcript, reaching them also paged in history.
  const showStatus = hasCapability(capabilities, 'status');
  const panelHas = panelContent({
    fieldCount: settingFields(settings, canStyle).length,
    showStatus,
    statusLine: detail?.statusLine,
    rateLimits: detail?.rateLimits,
    mighty: Boolean(mighty),
  });
  const questionKey = questionRequest ? requestKey(questionRequest) : undefined;
  const panelView = {
    expanded: panelExpanded,
    keyboardShown,
    questionPending,
    peeking: questionKey !== undefined && panelPeekFor === questionKey,
  };
  const panelOpen = panelShownExpanded(panelView);
  const togglePanel = () => {
    const next = toggledPanel(panelView);
    if (next.dismissKeyboard) {
      Keyboard.dismiss();
      // A hide event that never comes would leave the panel compact for good.
      clearTimeout(keyboardCheck.current);
      keyboardCheck.current = setTimeout(() => setKeyboardShown(Keyboard.isVisible()), 300);
    }
    setPanelPeekFor(next.peeking ? questionKey : undefined);
    // A peek leaves the remembered choice alone, so it is not even rewritten.
    if (next.expanded !== panelExpanded) setPanelExpanded(next.expanded);
  };

  // The top of the transcript holds only the history note. It is a button too, so a
  // transcript too short to scroll can still page in what came before it.
  const headerNode = detail ? (
    <View>
      {view === 'log' && loadingOlder ? (
        <View style={styles.olderRow}>
          <ActivityIndicator size="small" />
          <Text style={styles.olderText}>{t('phone.session.loadingOlder')}</Text>
        </View>
      ) : view === 'log' && canLoadOlder ? (
        <Pressable accessibilityRole="button" hitSlop={8} onPress={() => void loadOlder()}>
          <Text style={styles.olderText}>{t('phone.session.loadOlder')}</Text>
        </Pressable>
      ) : null}
    </View>
  ) : null;

  const footerNode = detail ? (
    <View style={styles.footer}>
      {pendingNode}
      {detail.permissions
        .filter((permission) => permission.questionnaire === undefined && !isPlanRequest(permission))
        .map((permission) => {
          const actions = antigravityActions(permission);
          return actions ? (
            <AntigravityPermissionCard
              key={permission.id}
              permission={permission}
              actions={actions}
              busy={deciding}
              error={antigravityErrors.get(requestKey(permission))}
              onAnswer={(decision) => void answerAntigravity(permission, decision)}
            />
          ) : (
            <PermissionCard
              key={permission.id}
              permission={permission}
              busy={deciding}
              onDecide={(allow) => void decide(permission.id, permission.runId, allow)}
            />
          );
        })}

      <QueuedList
        items={detail.queued}
        manageable={canQueue}
        canRunNext={!running}
        busy={queueBusy}
        flash={queueFlash}
        onRemove={removeQueued}
        onRunNext={runNext}
      />
    </View>
  ) : null;

  return (
    // Padding on both platforms: Android draws edge to edge (targetSdk 36), so the window
    // no longer shrinks for the keyboard and the view has to make the room itself. The
    // offset is the native header above this view, which the keyboard's position counts.
    <KeyboardAvoidingView
      behavior="padding"
      keyboardVerticalOffset={headerHeight}
      style={styles.screen}
    >
      <Stack.Screen
        options={{
          title: session?.title || t('phone.session.title'),
          // The compact header: the title, and under it the status glyph and the figures.
          headerTitle: detail
            ? () => <SessionTitle detail={detail} {...(receivedAt !== undefined ? { receivedAt } : {})} />
            : undefined,
          headerTitleAlign: 'center',
          headerRight: canPane
            ? () => (
                <Pressable
                  accessibilityLabel={t('pane.menu.accessibility')}
                  accessibilityRole="button"
                  hitSlop={8}
                  onPress={() => setMenuOpen(true)}
                  style={styles.headerCircle}
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

      <GestureDetector
        gesture={
          blocksShown ? blockFollow.pullGesture : structureShown ? structureFollow.pullGesture : logFollow.pullGesture
        }
      >
        <View collapsable={false} style={styles.body}>
          {view === 'structure' && mighty ? (
            <MightyStructureView
              state={shownStructureState}
              hasRuns={mighty.runs.length > 0}
              unfolded={unfoldedCards}
              onToggleFold={toggleCardFolded}
              contentContainerStyle={styles.list}
              headerStyle={styles.listHeader}
              header={headerNode}
              footer={footerNode}
              attachAs={structureFollow.attachAs}
              follow={structureFollow.props}
              ownSend={ownMove}
            />
          ) : view === 'blocks' && mighty ? (
            <MightyRunList
              runs={shownRuns}
              contentContainerStyle={styles.list}
              headerStyle={styles.listHeader}
              header={headerNode}
              footer={footerNode}
              listRef={blockFollow.attach}
              follow={blockFollow.props}
            />
          ) : (
            <FlatList
              {...logFollow.props}
              ref={logFollow.attach}
              data={entries}
              keyExtractor={(entry) => entry.id}
              extraData={entriesExtra}
              renderItem={({ item }) =>
                logStandIns.has(item.id) ? (
                  <PendingRequestView request={logStandIns.get(item.id)!} look="bubble" />
                ) : nextActions?.entryId === item.id ? (
                  <View>
                    <LogEntryView entry={item} antigravity={antigravity} />
                    <NextActionChips actions={nextActions.actions} onFill={fillComposer} />
                  </View>
                ) : (
                  <LogEntryView entry={item} antigravity={antigravity} />
                )
              }
              contentContainerStyle={styles.list}
              keyboardShouldPersistTaps="handled"
              maintainVisibleContentPosition={holdingPosition || logFollow.detached ? HOLD_FIRST_ROW : undefined}
              ListHeaderComponent={headerNode}
              ListHeaderComponentStyle={styles.listHeader}
              ListEmptyComponent={
                <EmptyState title={poll.loading ? t('phone.workspaces.loading') : t('phone.session.empty')} />
              }
              ListFooterComponent={footerNode}
            />
          )}
        </View>
      </GestureDetector>

      {refreshing || pull !== 'idle' ? (
        <View style={styles.pullRow}>
          {refreshing ? <ActivityIndicator color={palette.textFaint} size="small" /> : null}
          <Text style={styles.olderText}>
            {t(
              refreshing
                ? 'phone.session.refreshing'
                : pull === 'armed'
                  ? 'phone.session.releaseToRefresh'
                  : 'phone.session.pullToRefresh',
            )}
          </Text>
        </View>
      ) : null}

      <View style={{ paddingBottom: (keyboardShown ? 0 : insets.bottom) + spacing.sm }}>
        {questionRequest?.questionnaire ? (
          <QuestionnaireCard
            key={requestKey(questionRequest)}
            permission={questionRequest}
            questionnaire={questionRequest.questionnaire}
            busy={deciding}
            waiting={questionRequests.length}
            maxBodyHeight={questionBodyHeight}
            onCancel={() =>
              void decide(questionRequest.id, questionRequest.runId, false, t('phone.questionnaire.cancelled'))
            }
            onAnswer={(answers) => void answer(questionRequest.id, questionRequest.runId, answers)}
          />
        ) : null}
        {!questionPending && planRequest ? (
          <PlanCard
            key={requestKey(planRequest)}
            permission={planRequest}
            busy={deciding}
            maxBodyHeight={questionBodyHeight}
            onAnswer={(decision, feedback) => void answerPlan(planRequest.id, planRequest.runId, decision, feedback)}
          />
        ) : null}
        {!questionPending && !planPending && panel ? (
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
        {(blocksShown || structureShown) && nextActions ? (
          <View style={styles.dockedNext}>
            <NextActionChips actions={nextActions.actions} onFill={fillComposer} />
          </View>
        ) : null}
        {detail && (panelHas.body || panelHas.viewSwitch) ? (
          <SessionPanel
            summary={settingsSummary(settings, t('phone.session.panel.title'))}
            expanded={panelOpen}
            maxHeight={Math.round(windowHeight * 0.35)}
            onToggle={togglePanel}
            accessory={
              panelHas.viewSwitch ? (
                <SegmentedControl
                  options={[
                    { id: 'log', label: t('phone.session.view.log'), icon: 'log' },
                    { id: 'blocks', label: t('phone.session.view.blocks'), icon: 'flow' },
                    { id: 'structure', label: t('phone.session.view.structure'), icon: 'pipeline' },
                  ]}
                  value={view}
                  onChange={setChosenView}
                />
              ) : undefined
            }
          >
            {panelHas.body ? (
              <SessionHeader
                detail={detail}
                settings={settings}
                showStatus={showStatus}
                styleAware={canStyle}
                onEditSetting={openPicker}
              />
            ) : undefined}
          </SessionPanel>
        ) : null}
        <Composer
          text={text}
          onChangeText={changeText}
          disabled={!client || session === undefined}
          terminal={session?.terminal ?? false}
          running={running}
          sending={sending}
          submitModes={hasCapability(capabilities, 'submit-mode')}
          commands={canCommands ? commands : []}
          attachments={canAttach && session?.kind === 'claude' ? attachments : undefined}
          onSend={send}
          stopping={stopAction.state}
          {...(commandRun ? { command: commandRun } : {})}
          onStop={stop}
          onCommand={onCommand}
          inputRef={composerInput}
        />
      </View>

      <Sheet visible={menuOpen} onClose={() => setMenuOpen(false)}>
        <Text style={styles.menuTitle}>{t('phone.session.menu.title')}</Text>
        <Button
          label={t('phone.session.menu.rename')}
          tone="neutral"
          onPress={() => {
            setMenuOpen(false);
            setRenaming(true);
          }}
        />
        <Button
          label={t('phone.session.menu.close')}
          tone="danger"
          onPress={() => {
            setMenuOpen(false);
            setClosing(true);
          }}
        />
        <Button label={t('common.cancel')} tone="ghost" onPress={() => setMenuOpen(false)} />
      </Sheet>

      <PromptDialog
        visible={renaming}
        title={t('phone.session.menu.rename')}
        description={
          session?.titleMode === 'fixed'
            ? `${t('pane.rename.modeFixed')} · ${t('phone.session.rename.limit')}`
            : `${t('pane.rename.automatic')} · ${t('phone.session.rename.limit')}`
        }
        initialValue={session?.title ?? ''}
        placeholder={t('phone.session.rename.placeholder')}
        confirmLabel={t('phone.session.rename.confirm')}
        busy={paneBusy}
        onConfirm={rename}
        onCancel={() => setRenaming(false)}
        extraAction={
          session?.titleMode === 'fixed'
            ? { label: t('pane.rename.backToAutomatic'), onPress: setAutoTitle }
            : undefined
        }
      />

      <ConfirmDialog
        visible={closing}
        title={t('phone.session.close.title')}
        description={t('phone.session.close.message')}
        confirmLabel={t('common.close')}
        destructive
        busy={paneBusy}
        onConfirm={closePane}
        onCancel={() => setClosing(false)}
      />

      <PickerSheet
        visible={picker !== undefined}
        title={picker ? settingTitle(picker) : ''}
        note={
          settings && !settings.editable
            ? t('phone.session.settingsLocked')
            : pendingViewMode
              ? t('phone.session.applyWithMighty')
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
    // A short transcript sits at the bottom, right above the composer, as in a chat: the
    // header takes the spare room and stays at the top, the rows and footer end the list.
    list: {
      flexGrow: 1,
      justifyContent: 'flex-end',
      paddingBottom: spacing.xl,
      paddingHorizontal: spacing.lg,
      paddingTop: spacing.sm,
    },
    listHeader: { flexGrow: 1 },
    body: { flex: 1 },
    footer: { gap: spacing.sm, paddingTop: spacing.sm },
    dockedNext: { paddingHorizontal: spacing.lg, paddingTop: spacing.xs },
    headerAction: {
      color: palette.text,
      fontSize: 18,
      fontWeight: '800',
      lineHeight: 22,
      textAlign: 'center',
    },
    headerCircle: {
      alignItems: 'center',
      backgroundColor: palette.surface,
      borderRadius: 18,
      height: 36,
      justifyContent: 'center',
      width: 36,
    },
    menuTitle: { ...typeScale.title, color: palette.text },
    olderRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm },
    olderText: { color: palette.textFaint, fontSize: 12, paddingVertical: spacing.xs },
    pullRow: { alignItems: 'center', flexDirection: 'row', gap: spacing.sm, justifyContent: 'center' },
  });
