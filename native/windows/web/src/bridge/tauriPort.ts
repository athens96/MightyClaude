// The page's host in the Rust + Tauri app (native/tauri/src-tauri/src/host.rs): every message to the host goes through the
// `host_post` command, and every message from it — a call's result and every push — arrives on the `host-message` event, in the
// order the host posted them. The WebView2 host (chrome.webview) is not used here: in Tauri that channel carries Tauri's own IPC.
import type { HostPort } from './bridge';

export const HOST_POST = 'host_post';
export const HOST_MESSAGE = 'host-message';

/** The two Tauri calls the port makes; a test hands in its own. */
export interface TauriApi {
  invoke(command: string, args: Record<string, unknown>): Promise<unknown>;
  listen(event: string, handler: (event: { payload: unknown }) => void): Promise<unknown>;
}

// Loaded when first used, so a build that never uses the port (the C# app's, bridge.ts) holds nothing of @tauri-apps/api.
const tauri: TauriApi = {
  invoke: async (command, args) => (await import('@tauri-apps/api/core')).invoke(command, args),
  listen: async (event, handler) => (await import('@tauri-apps/api/event')).listen(event, handler),
};

/**
 * A {@link HostPort} over Tauri. Registering the event listener is asynchronous, so every post waits behind it: nothing reaches
 * the host before the page can hear the answer, and posts go one after another, in the order they were made (each invoke is a
 * request of its own, which could otherwise overtake another). A post that cannot be delivered answers its call with a failed
 * result, so the call rejects instead of waiting for ever.
 */
export function tauriPort(api: TauriApi = tauri): HostPort {
  const listeners: ((event: { data: unknown }) => void)[] = [];
  const deliver = (data: unknown) => listeners.forEach((listener) => listener({ data }));
  let broken: unknown = null;
  let tail: Promise<void> = api.listen(HOST_MESSAGE, (event) => deliver(event.payload)).then(
    () => undefined,
    (error: unknown) => {
      broken = error;
    },
  );
  const fail = (message: unknown, error: unknown) => {
    const call = message as { kind?: unknown; id?: unknown };
    if (call?.kind === 'call' && typeof call.id === 'number') deliver({ kind: 'result', id: call.id, ok: false, error: 'host unreachable: ' + String(error) });
  };
  return {
    postMessage(message) {
      tail = tail
        .then(() => (broken === null ? api.invoke(HOST_POST, { message }) : Promise.reject(broken)))
        .then(
          () => undefined,
          (error: unknown) => fail(message, error),
        );
    },
    addEventListener(_type, listener) {
      listeners.push(listener);
    },
  };
}

/** Whether the page runs in the Tauri app (its webview carries Tauri's internals). */
export function inTauri(): boolean {
  return '__TAURI_INTERNALS__' in globalThis;
}
