import type { CallMessage, HostMessage, PushMessage } from './protocol';

/** The two ends of chrome.webview the bridge uses; a test or the dev server hands in its own. */
export interface HostPort {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

interface Pending {
  resolve(value: unknown): void;
  reject(error: Error): void;
}

/** Calls into the host and hands every push to one listener. */
export class Bridge {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private push: (message: PushMessage) => void = () => {};

  constructor(private readonly port: HostPort) {
    port.addEventListener('message', (event) => this.receive(event.data));
  }

  onPush(listener: (message: PushMessage) => void): void {
    this.push = listener;
  }

  call<T = unknown>(method: string, args?: unknown): Promise<T> {
    const id = this.nextId++;
    const message: CallMessage = args === undefined ? { kind: 'call', id, method } : { kind: 'call', id, method, args };
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
      try {
        this.port.postMessage(message);
      } catch (error) {
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private receive(data: unknown): void {
    if (typeof data !== 'object' || data === null || !('kind' in data)) return;
    const message = data as HostMessage;
    if (message.kind === 'result') {
      const waiting = this.pending.get(message.id);
      if (!waiting) return;
      this.pending.delete(message.id);
      if (message.ok) waiting.resolve(message.value);
      else waiting.reject(new Error(message.error ?? message.id.toString()));
      return;
    }
    this.push(message);
  }
}

interface WebViewWindow {
  chrome?: { webview?: HostPort };
}

/** The page's host: WebView2's chrome.webview, or null in a plain browser (the dev server). */
export function hostPort(): HostPort | null {
  return (globalThis as unknown as WebViewWindow).chrome?.webview ?? null;
}
