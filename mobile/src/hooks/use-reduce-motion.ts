import { useSyncExternalStore } from 'react';
import { AccessibilityInfo } from 'react-native';

/**
 * The system "reduce motion" switch, held once for everything on screen that moves: one
 * AccessibilityInfo listener while any listener is mounted, none once the last one goes.
 * Reduce Motion takes the movement away only; when a mark shows and hides stays the same.
 */
let reduceMotion = false;
const reduceMotionListeners = new Set<() => void>();
let reduceMotionSubscription: { remove: () => void } | undefined;

function setReduceMotion(value: boolean): void {
  if (value === reduceMotion) return;
  reduceMotion = value;
  reduceMotionListeners.forEach((listener) => listener());
}

function subscribeReduceMotion(listener: () => void): () => void {
  reduceMotionListeners.add(listener);
  if (!reduceMotionSubscription) {
    reduceMotionSubscription = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduceMotion);
    // Read afresh on every first subscriber: the switch may have moved while none listened.
    void AccessibilityInfo.isReduceMotionEnabled().then(setReduceMotion);
  }
  return () => {
    reduceMotionListeners.delete(listener);
    if (reduceMotionListeners.size === 0) {
      reduceMotionSubscription?.remove();
      reduceMotionSubscription = undefined;
    }
  };
}

export function useReduceMotion(): boolean {
  return useSyncExternalStore(subscribeReduceMotion, () => reduceMotion);
}
