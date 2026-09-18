import { createKeyedMutex } from '@/lib/keyed-mutex';

/** Lets the queued `.then` callbacks run before the next assertion. */
const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

describe('createKeyedMutex', () => {
  it('lets only one holder into a key at a time', async () => {
    const mutex = createKeyedMutex();
    const first = await mutex.acquire('mac');
    expect(mutex.isHeld('mac')).toBe(true);

    let secondIn = false;
    const second = mutex.acquire('mac').then((release) => {
      secondIn = true;
      return release;
    });

    await settle();
    expect(secondIn).toBe(false);

    first();
    const release = await second;
    expect(secondIn).toBe(true);
    release();
    expect(mutex.isHeld('mac')).toBe(false);
  });

  it('does not make one host wait for another', async () => {
    const mutex = createKeyedMutex();
    const mac = await mutex.acquire('mac');
    const studio = await mutex.acquire('studio');
    expect(mutex.isHeld('mac')).toBe(true);
    expect(mutex.isHeld('studio')).toBe(true);
    mac();
    studio();
  });

  it('is the gate the pairing key needs: the waiter reads what the holder stored', async () => {
    // The shape of the real race: two connections dial the same host at once and both
    // would authenticate with the pairing key, so the host would mint two tokens.
    const mutex = createKeyedMutex();
    const stored: { token?: string } = {};
    const authenticatedWith: string[] = [];

    const authenticate = (mintedToken: string) =>
      mutex.run('mac', async () => {
        if (stored.token) {
          authenticatedWith.push(stored.token);
          return;
        }
        authenticatedWith.push('pairing-key');
        await settle();
        stored.token = mintedToken;
      });

    await Promise.all([authenticate('token-a'), authenticate('token-b')]);

    expect(authenticatedWith).toEqual(['pairing-key', 'token-a']);
    expect(stored.token).toBe('token-a');
  });

  it('hands the key on even when the task throws', async () => {
    const mutex = createKeyedMutex();
    await expect(
      mutex.run('mac', () => Promise.reject(new Error('인증 실패'))),
    ).rejects.toThrow('인증 실패');
    expect(mutex.isHeld('mac')).toBe(false);
    await expect(mutex.run('mac', () => Promise.resolve('ok'))).resolves.toBe('ok');
  });

  it('ignores a release called twice', async () => {
    const mutex = createKeyedMutex();
    const release = await mutex.acquire('mac');
    release();
    release();

    const order: string[] = [];
    const a = mutex.acquire('mac').then((done) => {
      order.push('a');
      return done;
    });
    const b = mutex.acquire('mac').then((done) => {
      order.push('b');
      return done;
    });
    await settle();
    expect(order).toEqual(['a']);
    (await a)();
    (await b)();
    expect(order).toEqual(['a', 'b']);
  });

  it('runs the queue in the order it formed', async () => {
    const mutex = createKeyedMutex();
    const order: number[] = [];
    await Promise.all(
      [1, 2, 3, 4].map((n) =>
        mutex.run('mac', async () => {
          order.push(n);
          await settle();
        }),
      ),
    );
    expect(order).toEqual([1, 2, 3, 4]);
    expect(mutex.isHeld('mac')).toBe(false);
  });
});
