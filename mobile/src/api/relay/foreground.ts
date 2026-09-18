import { AppState } from 'react-native';
import type { ForegroundSignal } from '@/api/relay/transport';

/** Bridges React Native's AppState so connections redial the moment the app returns. */
export const appForeground: ForegroundSignal = {
  subscribe(listener) {
    const subscription = AppState.addEventListener('change', (status) => {
      if (status === 'active') listener();
    });
    return () => subscription.remove();
  },
};
