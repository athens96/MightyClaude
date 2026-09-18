import { LeasePool, type PoolIdentity } from '@/lib/connection-pool';

/** A tunnel that only records whether it was closed, and what it authenticates with. */
class FakeConnection {
  closed = false;
  constructor(
    public deviceToken: string | undefined = undefined,
    public clientId: string | undefined = undefined,
  ) {}

  close(): void {
    this.closed = true;
  }
}

/** Collects the queued closes so a test can decide when the beat passes. */
function manualDefer() {
  const queued: Array<() => void> = [];
  return {
    defer: (run: () => void) => queued.push(run),
    flush: () => {
      for (const run of queued.splice(0)) run();
    },
    get size() {
      return queued.length;
    },
  };
}

function identity(overrides: Partial<PoolIdentity> = {}): PoolIdentity {
  return { fingerprint: 'mac', secret: 'key-1', clientId: 'c1', ...overrides };
}

function poolWith(defer: (run: () => void) => void) {
  const made: FakeConnection[] = [];
  const pool = new LeasePool<FakeConnection, string>(defer);
  const take = (id: PoolIdentity, connection?: FakeConnection) =>
    pool.acquire('mac', id, () => {
      const made_ = connection ?? new FakeConnection();
      made.push(made_);
      return { connection: made_, value: `client-${made.length}` };
    });
  return { pool, made, take };
}

describe('LeasePool', () => {
  it('hands every leaseholder the same connection and closes it once, later', () => {
    const timer = manualDefer();
    const { pool, made, take } = poolWith(timer.defer);

    const first = take(identity());
    const second = take(identity());
    expect(made).toHaveLength(1);
    expect(first.value).toBe(second.value);

    first.release();
    timer.flush();
    expect(made[0]?.closed).toBe(false);

    second.release();
    expect(made[0]?.closed).toBe(false);
    timer.flush();
    expect(made[0]?.closed).toBe(true);
    expect(pool.peek('mac')).toBeUndefined();
  });

  it('keeps the tunnel when a screen releases and re-leases in the same commit', () => {
    // React tears an effect down before it sets the next one up, so this is what a
    // credential change looks like from here: release, then acquire, back to back.
    const timer = manualDefer();
    const { made, take } = poolWith(timer.defer);
    const first = take(identity());

    first.release();
    const second = take(identity());
    timer.flush();

    expect(made).toHaveLength(1);
    expect(made[0]?.closed).toBe(false);
    expect(second.connection).toBe(made[0]);
  });

  it('adopts the token the live tunnel was just handed instead of dialling again', () => {
    const timer = manualDefer();
    const live = new FakeConnection(undefined, 'c1');
    const { made, take } = poolWith(timer.defer);
    const first = take(identity(), live);

    // The host answers `auth_ok` with a token; the store then offers it as credentials.
    live.deviceToken = 'token-1';
    first.release();
    const second = take(identity({ secret: 'token-1' }));
    timer.flush();

    expect(made).toHaveLength(1);
    expect(live.closed).toBe(false);
    expect(second.connection).toBe(live);
  });

  it('replaces the tunnel when the secret is one it never held', () => {
    const timer = manualDefer();
    const { made, take } = poolWith(timer.defer);
    const first = take(identity());
    first.release();
    const second = take(identity({ secret: 'a-different-key' }));
    timer.flush();

    expect(made).toHaveLength(2);
    expect(made[0]?.closed).toBe(true);
    expect(second.connection).toBe(made[1]);
  });

  it('replaces the tunnel when the relay or the host key changes', () => {
    const timer = manualDefer();
    const { made, take } = poolWith(timer.defer);
    take(identity());
    take(identity({ fingerprint: 'other-relay' }));
    timer.flush();
    expect(made).toHaveLength(2);
    expect(made[0]?.closed).toBe(true);
  });

  it('adopts a clientId the tunnel minted for itself after a conflict', () => {
    const timer = manualDefer();
    const live = new FakeConnection(undefined, 'c1');
    const { made, take } = poolWith(timer.defer);
    const first = take(identity(), live);

    // The host answered `device-conflict`, so the tunnel introduced itself anew.
    live.clientId = 'c-host-specific';
    first.release();
    const second = take(identity({ clientId: 'c-host-specific' }));
    timer.flush();

    expect(made).toHaveLength(1);
    expect(second.connection).toBe(live);
    expect(live.closed).toBe(false);
  });

  it('closes on demand and ignores the queued close of a connection already gone', () => {
    const timer = manualDefer();
    const { pool, made, take } = poolWith(timer.defer);
    const lease = take(identity());

    pool.close('mac');
    expect(made[0]?.closed).toBe(true);

    const replacement = take(identity());
    expect(made).toHaveLength(2);

    // The first lease's release must not take the replacement down with it.
    lease.release();
    timer.flush();
    expect(made[1]?.closed).toBe(false);
    expect(pool.peek('mac')).toBe(replacement.connection);
  });

  it('ignores a release called twice', () => {
    const timer = manualDefer();
    const { made, take } = poolWith(timer.defer);
    const first = take(identity());
    const second = take(identity());

    first.release();
    first.release();
    timer.flush();
    expect(made[0]?.closed).toBe(false);

    second.release();
    timer.flush();
    expect(made[0]?.closed).toBe(true);
  });
});
