import { AppState } from 'react-native';
import type { ForegroundSignal } from '@/api/relay/transport';
import { returnedToForeground } from '@/lib/resync';

/** Bridges React Native's AppState so connections redial the moment the app returns. */
export const appForeground: ForegroundSignal = {
  subscribe(listener) {
    let previous: string | null = AppState.currentState;
    const subscription = AppState.addEventListener('change', (status) => {
      const returned = returnedToForeground(previous, status);
      previous = status;
      if (returned) listener();
    });
    return () => subscription.remove();
  },
};
