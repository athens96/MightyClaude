import { useEffect, useMemo, useRef, useState } from 'react';
import { AppState } from 'react-native';
import * as Clipboard from 'expo-clipboard';
import * as Network from 'expo-network';
import type { MobileClient } from '@/api/client';
import {
  createScreenShareController,
  type ScreenShareController,
  type ScreenShareSnapshot,
} from '@/lib/screen-share/controller';
import { nativeControlKey } from '@/lib/screen-share/native-control-key';
import { networkFor } from '@/lib/screen-share/quality';
import { controlPromptCancelText, controlPromptText } from '@/lib/screen-share/strings';
import { createWebrtcScreenPeer, videoDecoderCapabilities } from '@/lib/screen-share/webrtc-peer';
import { nativeZstd } from '@/lib/screen-share/zstd';

/**
 * Wires one host's screen-share controller to the phone's own parts: the radio it is on,
 * the Keystore control key, the clipboard, and the app's foreground state. The 1 s tick
 * lets the connect deadline, the idle ceiling and the phone's own 30 s background rule
 * fire; the Mac is told about the background at once and keeps the same rule itself.
 */

const TICK_MS = 1000;

export interface ScreenShareHook {
  controller: ScreenShareController | undefined;
  snapshot: ScreenShareSnapshot | undefined;
}

export function useScreenShare(
  hostId: string | undefined,
  client: MobileClient | undefined,
): ScreenShareHook {
  const [snapshot, setSnapshot] = useState<ScreenShareSnapshot | undefined>(undefined);
  const controllerRef = useRef<ScreenShareController | undefined>(undefined);

  const controller = useMemo(() => {
    if (!hostId || !client) return undefined;
    return createScreenShareController({
      client,
      peerFactory: createWebrtcScreenPeer,
      controlKey: {
        hostId,
        native: nativeControlKey(),
        prompt: { title: controlPromptText(), cancel: controlPromptCancelText() },
      },
      now: () => Date.now(),
      network: async () => {
        try {
          const state = await Network.getNetworkStateAsync();
          return networkFor(state.type);
        } catch {
          // Unknown is treated as Wi-Fi's ceiling only after the Mac agrees; the Mac
          // caps the bitrate itself, so a wrong guess here costs nothing permanent.
          return 'wifi';
        }
      },
      decoders: videoDecoderCapabilities,
      zstd: nativeZstd(),
      readClipboard: () => Clipboard.getStringAsync(),
      writeClipboard: (text) => Clipboard.setStringAsync(text).then(() => undefined),
    });
  }, [client, hostId]);

  controllerRef.current = controller;

  useEffect(() => {
    if (!controller) {
      setSnapshot(undefined);
      return undefined;
    }
    setSnapshot(controller.snapshot());
    const unsubscribe = controller.subscribe(setSnapshot);
    void controller.refresh();
    const timer = setInterval(() => controller.tick(), TICK_MS);
    const appState = AppState.addEventListener('change', (next) => {
      if (next === 'active') controller.foreground();
      else controller.background();
    });
    return () => {
      clearInterval(timer);
      appState.remove();
      unsubscribe();
      controller.dispose();
    };
  }, [controller]);

  return { controller, snapshot };
}
