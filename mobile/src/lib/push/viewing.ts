/** How often the phone repeats "this session is on screen"; the host holds it for 45 s. */
export const VIEWING_REFRESH_MS = 30_000;

export interface ViewingDeps {
  send(storageId: string, sessionId: string | null): Promise<void>;
  setInterval(run: () => void, ms: number): unknown;
  clearInterval(handle: unknown): void;
}

/**
 * Tells a host which of its sessions this phone shows, so it does not push about it
 * (`POST /m1/push/viewing`). It says so on entering, repeats it before the host's 45 s
 * run out, and clears it on leaving or when the app goes to the background. Best effort.
 */
export function createViewingReporter(deps: ViewingDeps) {
  let current: { storageId: string; sessionId: string } | null = null;
  let active = true;
  let timer: unknown = null;

  const say = (storageId: string, sessionId: string | null): void => {
    void deps.send(storageId, sessionId).catch(() => undefined);
  };
  const stop = (): void => {
    if (timer !== null) deps.clearInterval(timer);
    timer = null;
  };
  const start = (): void => {
    stop();
    if (current === null || !active) return;
    const { storageId, sessionId } = current;
    say(storageId, sessionId);
    timer = deps.setInterval(() => say(storageId, sessionId), VIEWING_REFRESH_MS);
  };

  return {
    enter(storageId: string, sessionId: string): void {
      if (current !== null && current.storageId !== storageId && active) say(current.storageId, null);
      current = { storageId, sessionId };
      start();
    },
    leave(): void {
      stop();
      if (current !== null && active) say(current.storageId, null);
      current = null;
    },
    setActive(next: boolean): void {
      if (active === next) return;
      active = next;
      if (current === null) return;
      if (next) {
        start();
      } else {
        stop();
        say(current.storageId, null);
      }
    },
  };
}
