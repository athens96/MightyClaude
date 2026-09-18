/**
 * One waiting line per key. Two things in this app must never run twice at once for the
 * same host: the pairing-key authentication (docs/relay.md "기기 토큰" — the host issues a
 * device token once per `auth`, so two of them mint two tokens and the loser's is lost)
 * and the write that stores such a token.
 *
 * Pure on purpose: nothing here knows about hosts, sockets or the keychain, so the rule
 * can be tested with made-up keys and resolved promises.
 */

/** Hands the key to whoever is next; calling it twice is a no-op. */
export type Release = () => void;

export interface KeyedMutex {
  /** Waits for the key to be free and answers with its release. Call the release once. */
  acquire(key: string): Promise<Release>;
  /** Runs `task` holding the key, releasing it however the task ends. */
  run<T>(key: string, task: () => Promise<T>): Promise<T>;
  /** True while someone holds the key. */
  isHeld(key: string): boolean;
}

export function createKeyedMutex(): KeyedMutex {
  /** The promise that resolves when the last queued holder lets go. */
  const tails = new Map<string, Promise<void>>();
  /** Holders plus everyone still queued, so a key can be forgotten when it empties. */
  const pending = new Map<string, number>();
  const held = new Set<string>();

  const acquire = (key: string): Promise<Release> => {
    pending.set(key, (pending.get(key) ?? 0) + 1);
    const previous = tails.get(key) ?? Promise.resolve();
    let letGo: () => void = () => undefined;
    const mine = new Promise<void>((resolve) => {
      letGo = resolve;
    });
    tails.set(
      key,
      previous.then(() => mine),
    );
    return previous.then(() => {
      held.add(key);
      let released = false;
      return () => {
        if (released) return;
        released = true;
        held.delete(key);
        const left = (pending.get(key) ?? 1) - 1;
        if (left > 0) {
          pending.set(key, left);
        } else {
          // Nobody is queued behind us, so the key can be dropped instead of keeping a
          // chain of settled promises alive for every host the app has ever reached.
          pending.delete(key);
          tails.delete(key);
        }
        letGo();
      };
    });
  };

  return {
    acquire,
    isHeld: (key) => held.has(key),
    run: async (key, task) => {
      const release = await acquire(key);
      try {
        return await task();
      } finally {
        release();
      }
    },
  };
}
