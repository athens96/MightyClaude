import {
  getMessaging,
  getToken,
  onMessage,
  onTokenRefresh,
  setAutoInitEnabled,
} from '@react-native-firebase/messaging';
import * as SecureStore from 'expo-secure-store';
import { PermissionsAndroid, Platform } from 'react-native';
import notifee, {
  AndroidImportance,
  AndroidVisibility,
  EventType,
  type Event,
  type Notification,
} from 'react-native-notify-kit';

import type { MobileClient } from '@/api/client';
import { hasCapability, parseCapabilities } from '@/lib/capabilities';
import { answerFromNotification } from '@/lib/push/actions';
import { isDeviceLocked } from '@/lib/push/device-lock';
import { loadedHosts, withHostClient } from '@/lib/push/host-lease';
import { parsePushMessage, type PushKind } from '@/lib/push/message';
import {
  CHANNEL_ATTENTION,
  CHANNEL_WORK,
  channelName,
  notificationData,
  notificationFor,
  outcomeUpdate,
  sessionPath,
  type PushAction,
  type PushNotificationSpec,
} from '@/lib/push/notification';
import { findHostByHandle, globalPushFetch, syncHostPush } from '@/lib/push/registry';
import { openPush, pushKey } from '@/lib/push/seal';
import { enabledKinds, pushSettings } from '@/lib/push/settings';
import { createViewingReporter } from '@/lib/push/viewing';
import { workspaceMute } from '@/lib/push/workspace-mute';

const ASKED_KEY = 'mightyclaude.push.asked.v1';
/** Quick switch flips and host changes within this long send one registration. */
export const SYNC_DEBOUNCE_MS = 1_000;
/** The first retry after a host or its relay could not be reached (429, 503, offline); it doubles each time. */
export const SYNC_RETRY_MS = 30_000;
/** Retries after one sync before waiting for the next start, switch, host or token change. */
export const SYNC_RETRIES = 5;
/** One host's sync: the tunnel, `info`, up to two relay calls and the registration. */
const SYNC_TIMEOUT_MS = 30_000;
/** A notification button's answer, the tunnel included. */
const RESPOND_TIMEOUT_MS = 15_000;
/** A tap can arrive both as an event and as the app's initial notification; the second is ignored. */
const OPEN_DEDUPE_MS = 5_000;

/**
 * Hidden on a secure lock screen when the user chose to hide sensitive content there: Android
 * then shows only the app's name. Titles and summaries quote the user's work, so they are never
 * PUBLIC; the user's own lock-screen choice decides.
 */
const VISIBILITY = AndroidVisibility.PRIVATE;

export async function ensureChannels(): Promise<void> {
  await notifee.createChannel({
    id: CHANNEL_ATTENTION,
    name: channelName(CHANNEL_ATTENTION),
    importance: AndroidImportance.HIGH,
    visibility: VISIBILITY,
  });
  await notifee.createChannel({
    id: CHANNEL_WORK,
    name: channelName(CHANNEL_WORK),
    importance: AndroidImportance.DEFAULT,
    visibility: VISIBILITY,
  });
}

/** The notification library reads its texts as HTML; a host's title such as `fix <Row>` must show as written. */
export function plainText(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

/** A body as written: escaped, with its line breaks kept (HTML would fold them into spaces). */
export function plainBody(text: string): string {
  return plainText(text).replace(/\r?\n/g, '<br>');
}

function toNotification(spec: PushNotificationSpec): Notification {
  return {
    id: spec.id,
    title: plainText(spec.title),
    // Android shows `subtitle` as the sub-text next to the app name: the host and workspace.
    subtitle: plainText(spec.subText),
    body: plainBody(spec.body),
    data: spec.data,
    android: {
      channelId: spec.channelId,
      visibility: VISIBILITY,
      pressAction: { id: 'default', launchActivity: 'default' },
      actions: spec.actions.map((action) => ({ title: action.title, pressAction: { id: action.id } })),
    },
  };
}

/**
 * An FCM data message → the host's sealed message → a local notification. The pairing is the
 * one whose handle (`h`) the push came through and whose key opens it; the sealed `hostId` never
 * picks it. Anything else, a kind switched off or a workspace muted on this phone, is dropped
 * without a word.
 */
export async function showPush(remote: { data?: Record<string, unknown> }): Promise<void> {
  try {
    const data = remote.data ?? {};
    const handle = data['h'];
    const payload = data['p'];
    if (data['v'] !== '1' || typeof handle !== 'string' || typeof payload !== 'string') return;
    const state = await loadedHosts();
    const storageId = await findHostByHandle(SecureStore, state.hosts.map((host) => host.id), handle);
    if (storageId === null) return;
    const token = state.tokens[storageId];
    const clientId = state.clientIds[storageId] ?? state.clientId;
    if (!token || !clientId) return;
    const opened = openPush(payload, pushKey(token, clientId), handle);
    const message = opened === null ? null : parsePushMessage(opened);
    if (message === null) return;
    await pushSettings.ready;
    if (!pushSettings.get().kinds[message.kind]) return;
    // A workspace muted on this phone drops every kind; one the phone cannot place is shown.
    if (!(await workspaceMute.shouldShow(storageId, message))) return;
    await ensureChannels();
    await notifee.displayNotification(toNotification(notificationFor(message, storageId, handle)));
  } catch {
    // A push that cannot be shown is dropped.
  }
}

let opener: ((path: string) => void) | null = null;
let pendingPath: string | null = null;
let lastOpened: { id: string; at: number } | null = null;

/** Where taps go once the app is up; a tap that came before (the app was closed) opens then. */
export function setPushOpener(open: ((path: string) => void) | null): void {
  opener = open;
  if (open === null || pendingPath === null) return;
  const path = pendingPath;
  pendingPath = null;
  open(path);
}

/** Opens the session a notification is about. */
export function openNotification(notification: Notification | undefined, open?: (path: string) => void): void {
  const data = notificationData(notification?.data);
  if (data === null) return;
  const id = notification?.id;
  if (id !== undefined) {
    const now = Date.now();
    if (lastOpened !== null && lastOpened.id === id && now - lastOpened.at < OPEN_DEDUPE_MS) return;
    lastOpened = { id, at: now };
  }
  const path = sessionPath(data);
  const target = open ?? opener;
  if (target === null) pendingPath = path;
  else target(path);
}

/**
 * A tap opens the session; a button answers from the background (docs/mobile-remote.md "푸시 알림"),
 * on the pairing the notification was shown for, and the notification then says what happened.
 */
export async function handleNotificationEvent({ type, detail }: Event, open?: (path: string) => void): Promise<void> {
  const notification = detail.notification;
  const data = notificationData(notification?.data);
  if (data === null || notification === undefined) return;
  if (type === EventType.PRESS) {
    openNotification(notification, open);
    return;
  }
  if (type !== EventType.ACTION_PRESS) return;
  const outcome = await answerFromNotification(
    {
      isDeviceLocked,
      respond: (storageId, sessionId, body) =>
        withHostClient(
          storageId,
          async (client, signal) => {
            await client.respondPermission(sessionId, body, signal);
          },
          RESPOND_TIMEOUT_MS,
        ),
      stillPending: (storageId, sessionId, requestId) =>
        withHostClient(
          storageId,
          async (client, signal) =>
            (await client.session(sessionId, { signal })).permissions.some(
              (permission) =>
                permission.id === requestId && (data.runId === undefined || permission.runId === data.runId),
            ),
          RESPOND_TIMEOUT_MS,
        ),
    },
    data,
    detail.pressAction?.id as PushAction,
  );
  const update = outcomeUpdate(outcome);
  try {
    await notifee.displayNotification({
      ...notification,
      body: plainBody(update.body),
      android: {
        ...notification.android,
        channelId: notification.android?.channelId ?? CHANNEL_ATTENTION,
        visibility: VISIBILITY,
        onlyAlertOnce: true,
        actions: update.keepActions ? (notification.android?.actions ?? []) : [],
        pressAction: { id: 'default', launchActivity: 'default' },
      },
    });
  } catch {
    // The answer stands; only its note on the notification is missing.
  }
}

async function askOnce(): Promise<void> {
  if (Platform.OS !== 'android' || Number(Platform.Version) < 33) return;
  if ((await SecureStore.getItemAsync(ASKED_KEY)) === '1') return;
  await SecureStore.setItemAsync(ASKED_KEY, '1');
  await PermissionsAndroid.request(PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS);
}

function messagingOrNull(): ReturnType<typeof getMessaging> | null {
  try {
    return getMessaging();
  } catch {
    return null; // A build without Firebase.
  }
}

let lastToken: string | undefined;
let tokenRequest: Promise<string | null> | null = null;

/**
 * This phone's FCM token, or null in a build without Firebase. firebase.json keeps FCM's auto-init
 * off, so the phone first talks to FCM when a paired host can push; asking turns it on from then on.
 */
function currentToken(): Promise<string | null> {
  if (lastToken !== undefined) return Promise.resolve(lastToken);
  tokenRequest ??= (async () => {
    const messaging = messagingOrNull();
    if (messaging === null) return null;
    await setAutoInitEnabled(messaging, true);
    lastToken = await getToken(messaging);
    return lastToken;
  })().finally(() => {
    tokenRequest = null;
  });
  return tokenRequest;
}

function syncHost(host: { id: string; relayUrl: string }, kinds: PushKind[]): Promise<'registered' | 'unsupported' | 'failed'> {
  return withHostClient(
    host.id,
    async (client, signal) => {
      const info = await client.info(signal);
      if (!hasCapability(parseCapabilities(info), 'push')) return 'unsupported';
      const token = await currentToken();
      if (token === null) return 'unsupported';
      const asked: Pick<MobileClient, 'info' | 'registerPush'> = {
        info: async () => info,
        registerPush: (input) => client.registerPush(input, signal),
      };
      return syncHostPush({ store: SecureStore, fetch: globalPushFetch, client: asked }, host, token, kinds);
    },
    SYNC_TIMEOUT_MS,
  );
}

/**
 * Gives every paired host that supports push this phone's current handle and kinds. False when
 * some host or its relay could not be reached.
 */
export async function syncAllHosts(fcmToken?: string): Promise<boolean> {
  if (fcmToken !== undefined) lastToken = fcmToken;
  const state = await loadedHosts();
  await pushSettings.ready;
  const kinds = enabledKinds(pushSettings.get());
  const results = await Promise.all(
    state.hosts
      .filter((host) => state.tokens[host.id])
      .map((host) => syncHost(host, kinds).catch(() => 'failed' as const)),
  );
  return !results.includes('failed');
}

let syncTimer: ReturnType<typeof setTimeout> | undefined;
let retries = 0;

function scheduleSync(delayMs: number): void {
  if (syncTimer !== undefined) clearTimeout(syncTimer);
  syncTimer = setTimeout(() => {
    syncTimer = undefined;
    void runSync();
  }, delayMs);
}

async function runSync(): Promise<void> {
  const done = await syncAllHosts().catch(() => false);
  if (done) {
    retries = 0;
    return;
  }
  // A newer request runs anyway; past the last retry the next change or start tries again.
  if (syncTimer !== undefined || retries >= SYNC_RETRIES) return;
  scheduleSync(SYNC_RETRY_MS * 2 ** retries);
  retries += 1;
}

/** Syncs every host after `delayMs` of quiet; a later request moves it and starts the retries over. */
export function requestSync(delayMs = SYNC_DEBOUNCE_MS): void {
  retries = 0;
  scheduleSync(delayMs);
}

/** Foreground start: channels, listeners and the token, then the one-time Android 13 prompt. Returns a cleanup. */
export async function startPush(): Promise<() => void> {
  await ensureChannels().catch(() => undefined);
  const messaging = messagingOrNull();
  if (messaging === null) {
    await askOnce().catch(() => undefined);
    return () => undefined;
  }
  // Listening starts before the permission prompt, so a push arriving while it is up is not lost.
  const stopToken = onTokenRefresh(messaging, (token) => {
    lastToken = token;
    requestSync();
  });
  const stopMessages = onMessage(messaging, (remote) => showPush(remote as { data?: Record<string, unknown> }));
  requestSync();
  await askOnce().catch(() => undefined);
  return () => {
    stopToken();
    stopMessages();
    if (syncTimer !== undefined) clearTimeout(syncTimer);
    syncTimer = undefined;
  };
}

/** The one viewing reporter: the session screen enters and leaves; app state pauses it. */
export const pushViewing = createViewingReporter({
  send: (storageId, sessionId) =>
    withHostClient(
      storageId,
      async (client, signal) => {
        await client.reportViewing(sessionId, signal);
      },
      10_000,
    ),
  setInterval: (run, ms) => setInterval(run, ms),
  clearInterval: (handle) => clearInterval(handle as ReturnType<typeof setInterval>),
});
