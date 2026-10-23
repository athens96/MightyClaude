import { router } from 'expo-router';
import { useEffect } from 'react';
import { AppState } from 'react-native';
import notifee from 'react-native-notify-kit';

import { enabledKinds, pushSettings } from '@/lib/push/settings';
import {
  handleNotificationEvent,
  openNotification,
  pushViewing,
  requestSync,
  setPushOpener,
  startPush,
} from '@/lib/push/runtime';
import { useHostsStore } from '@/store/hosts';

function openPath(path: string): void {
  try {
    router.push(path as never);
  } catch {
    // The navigator is not up yet; the notification stays in the shade to tap again.
  }
}

/**
 * Foreground push: start-up, taps, re-registering when the paired hosts or the notification
 * switches change, and pausing the viewing report in the background. Renders nothing.
 */
export function PushRoot() {
  // The pairings a push can come through: those holding a device token (the push key comes from it).
  const hostKey = useHostsStore((state) =>
    state.hosts
      .filter((host) => state.tokens[host.id])
      .map((host) => host.id)
      .join(','),
  );

  useEffect(() => {
    let stop: (() => void) | undefined;
    let gone = false;
    void startPush().then((cleanup) => {
      if (gone) cleanup();
      else stop = cleanup;
    });
    setPushOpener(openPath);
    void notifee
      .getInitialNotification()
      .then((initial) => openNotification(initial?.notification))
      .catch(() => undefined);
    const stopForeground = notifee.onForegroundEvent((event) => void handleNotificationEvent(event));
    // Only the kinds go to the hosts: the crash-report switch alone sends nothing.
    let kinds = enabledKinds(pushSettings.get()).join(',');
    const stopSettings = pushSettings.subscribe(() => {
      const next = enabledKinds(pushSettings.get()).join(',');
      if (next === kinds) return;
      kinds = next;
      requestSync();
    });
    const appState = AppState.addEventListener('change', (state) => {
      if (state !== 'inactive') pushViewing.setActive(state === 'active');
    });
    return () => {
      gone = true;
      stop?.();
      setPushOpener(null);
      stopForeground();
      stopSettings();
      appState.remove();
    };
  }, []);

  useEffect(() => {
    if (hostKey !== '') requestSync();
  }, [hostKey]);

  return null;
}
