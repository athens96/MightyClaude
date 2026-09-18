/**
 * Separator for the composite keys the app caches things under (`hostId|sessionId`, the
 * relay credential fingerprint). Host ids and session ids are validated identifiers —
 * letters, digits, `-`, `_` and `.` — so a `|` can never occur inside one and a host
 * prefix can never run into the next part.
 */
export const KEY_SEPARATOR = '|';
