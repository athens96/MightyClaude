/**
 * Which CLI a host runs for the `gemini` provider id. The id stays on the wire; the Mac
 * runs Google's Antigravity CLI (`agy`) for it from 0.4.23 and Windows from 0.4.28, and
 * calls it Antigravity. An older host still runs Gemini CLI. A host whose version this app
 * does not know yet (`/m1/info` not answered) or cannot read is taken to be current.
 */

/** What `/m1/info` says about the host: `darwin` or `win32`, and its app version. */
export interface HostIdentity {
  platform?: string;
  appVersion?: string;
}

/** The first release that runs Antigravity, per platform. */
const ANTIGRAVITY_SINCE: Record<string, readonly [number, number, number]> = {
  darwin: [0, 4, 23],
  win32: [0, 4, 28],
};

/** `0.4.23`, `0.4.23 (812)` or `0.4` as three numbers; undefined when it does not start like a version. */
export function parseAppVersion(value: string | undefined): [number, number, number] | undefined {
  const match = /^\s*v?(\d+)\.(\d+)(?:\.(\d+))?/.exec(value ?? '');
  if (!match) return undefined;
  return [Number(match[1]), Number(match[2]), Number(match[3] ?? 0)];
}

/** Whether the host's `gemini` panes run Antigravity (true unless the host is known to be older). */
export function hostRunsAntigravity(host: HostIdentity | undefined): boolean {
  const version = parseAppVersion(host?.appVersion);
  const since = host?.platform === undefined ? undefined : ANTIGRAVITY_SINCE[host.platform];
  if (!version || !since) return true;
  for (let index = 0; index < 3; index += 1) {
    if (version[index]! !== since[index]!) return version[index]! > since[index]!;
  }
  return true;
}
