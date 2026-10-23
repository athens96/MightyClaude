import { createContext, useContext, useSyncExternalStore } from 'react';
import type { Bridge } from '../bridge/bridge';
import type { Translate } from '../i18n/locale';
import type { Store, StoreState } from '../store/store';

export interface AppServices {
  bridge: Bridge;
  store: Store;
  t: Translate;
}

export const ServicesContext = createContext<AppServices | null>(null);

export function useServices(): AppServices {
  const services = useContext(ServicesContext);
  if (!services) throw new Error('ServicesContext is missing');
  return services;
}

/** The store's whole state; a component re-renders when it is replaced. */
export function useStoreState(): StoreState {
  const { store } = useServices();
  return useSyncExternalStore(store.subscribe, store.getState);
}
