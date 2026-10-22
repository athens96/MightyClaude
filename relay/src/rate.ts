/** A sliding-window counter per key: the same rule as the TURN mint limits in hub.ts. */
export type RateDecision =
  | { readonly ok: true }
  | { readonly ok: false; readonly retryAfterSecs: number };

export class SlidingWindowLimiter {
  readonly #windowMs: number;
  readonly #limit: number;
  readonly #times = new Map<string, number[]>();

  constructor(windowMs: number, limit: number) {
    this.#windowMs = windowMs;
    this.#limit = limit;
  }

  /** Whether one more event fits for `key`; nothing is recorded. */
  check(key: string, now: number): RateDecision {
    const times = this.#live(key, now);
    if (times.length < this.#limit) return { ok: true };
    const oldest = times[0] ?? now;
    return { ok: false, retryAfterSecs: Math.max(1, Math.ceil((oldest + this.#windowMs - now) / 1000)) };
  }

  record(key: string, now: number): void {
    const times = this.#live(key, now);
    times.push(now);
    this.#times.set(key, times);
  }

  /** Drops keys whose events all aged out, so the map cannot grow without bound. */
  sweep(now: number): void {
    for (const key of [...this.#times.keys()]) this.#live(key, now);
  }

  get size(): number {
    return this.#times.size;
  }

  #live(key: string, now: number): number[] {
    const times = (this.#times.get(key) ?? []).filter((t) => now - t < this.#windowMs);
    if (times.length === 0) this.#times.delete(key);
    else this.#times.set(key, times);
    return times;
  }
}
