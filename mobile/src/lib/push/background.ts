import { getMessaging, setBackgroundMessageHandler } from '@react-native-firebase/messaging';
import notifee from 'react-native-notify-kit';

import { installCrashReporting } from '@/lib/push/crash';
import { handleNotificationEvent, showPush } from '@/lib/push/runtime';

// Registered at the JS entry (index.js) so a message or a button that wakes a closed app finds them.
installCrashReporting();
try {
  setBackgroundMessageHandler(getMessaging(), (remote) => showPush(remote as { data?: Record<string, unknown> }));
} catch {
  // A build without Firebase: no pushes arrive, nothing to handle.
}
notifee.onBackgroundEvent((event) => handleNotificationEvent(event));
