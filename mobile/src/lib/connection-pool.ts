/**
 * One shared, reference-counted connection per key.
 *
 * Two rules earn their keep here, and both are easier to see without React in the way:
 *
 * 1. **The close is deferred.** React tears an effect down before it sets the next one
 *    up, so a screen whose credentials change releases the lease and re-takes it inside
 *    the same commit. Closing on the spot would drop a healthy tunnel and dial a new one
 *    every time; instead the close is queued and skipped if the count came back up.
 * 2. **A tunnel is adopted, not replaced, when it already holds the new secret.** The
 *    device token the host just issued arrives as "new credentials" although it is what
 *    the live connection is already authenticated with.
 */

/** What the pool needs of a connection: it can be closed, and it can say who it is. */
export interface PoolConnection {
  close(): void;
  /** The token this tunnel authenticates with right now, if it has one. */
  readonly deviceToken?: string | undefined;
  /** The id this tunnel introduced itself with, after any conflict retry. */
  readonly clientId?: string | undefined;
}

export interface PoolIdentity {
  /** Everything a change of which always means a different tunnel. */
  fingerprint: string;
  /** The device token or pairing key; empty when there is none. */
  secret: string;
  /** Empty on a build that has not generated one. */
  clientId: string;
}

export interface Lease<C, V> {
  connection: C;
  value: V;
  release: () => void;
}

/** Runs the queued close; replaced in tests so a run can be flushed on demand. */
export type Defer = (run: () => void) => void;

interface Entry<C, V> {
  identity: PoolIdentity;
  connection: C;
  value: V;
  refs: number;
}

const defaultDefer: Defer = (run) => {
  if (typeof queueMicrotask === 'function') queueMicrotask(run);
  else setTimeout(run, 0);
};

export class LeasePool<C extends PoolConnection, V> {
  private readonly entries = new Map<string, Entry<C, V>>();

  constructor(private readonly defer: Defer = defaultDefer) {}

  /** The live connection for a key, if the pool holds one. */
  peek(key: string): C | undefined {
    return this.entries.get(key)?.connection;
  }

  acquire(
    key: string,
    identity: PoolIdentity,
    create: () => { connection: C; value: V },
  ): Lease<C, V> {
    let entry = this.entries.get(key);
    if (entry && !adopts(entry, identity)) {
      this.entries.delete(key);
      entry.connection.close();
      entry = undefined;
    }
    if (entry) {
      entry.identity = identity;
    } else {
      const made = create();
      entry = { identity, connection: made.connection, value: made.value, refs: 0 };
      this.entries.set(key, entry);
    }
    entry.refs += 1;

    const leased = entry;
    let released = false;
    return {
      connection: leased.connection,
      value: leased.value,
      release: () => {
        if (released) return;
        released = true;
        leased.refs -= 1;
        if (leased.refs > 0) return;
        this.defer(() => {
          // A screen that re-leased in the same commit has put the count back up.
          if (leased.refs > 0) return;
          if (this.entries.get(key) !== leased) return;
          this.entries.delete(key);
          leased.connection.close();
        });
      },
    };
  }

  /** Drops a key's connection at once, e.g. after unpairing. */
  close(key: string): void {
    const entry = this.entries.get(key);
    if (!entry) return;
    this.entries.delete(key);
    entry.connection.close();
  }
}

/** Whether the pooled tunnel is the one these credentials ask for. */
function adopts<C extends PoolConnection, V>(entry: Entry<C, V>, identity: PoolIdentity): boolean {
  if (entry.identity.fingerprint !== identity.fingerprint) return false;
  if (
    identity.clientId !== entry.identity.clientId &&
    identity.clientId !== (entry.connection.clientId ?? '')
  ) {
    return false;
  }
  if (identity.secret === entry.identity.secret) return true;
  // The token this tunnel was just handed is not a new credential to reconnect for.
  return identity.secret.length > 0 && identity.secret === (entry.connection.deviceToken ?? '');
}
