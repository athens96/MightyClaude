import { useSyncExternalStore } from 'react';
import * as SecureStore from 'expo-secure-store';
import { createPanelState } from '@/lib/session-panel';

/**
 * Whether the session panel is open, remembered per device. The read starts when this
 * module loads, before any pane is on screen, and every mounted screen shares the one
 * value, so two panes can never disagree about it.
 */
const panelState = createPanelState(SecureStore);

export function usePanelExpanded(): [boolean, (expanded: boolean) => void] {
  const expanded = useSyncExternalStore(panelState.subscribe, panelState.get);
  return [expanded, panelState.set];
}
