import type { AppUpdatePhase, MobileAppUpdate } from '@/api/types';

const PHASES: ReadonlySet<string> = new Set<AppUpdatePhase>([
  'idle',
  'checking',
  'upToDate',
  'available',
  'downloading',
  'ready',
  'installing',
  'failed',
]);

/** Reads `MobileState.appUpdate`; an unknown phase or a malformed value counts as absent. */
export function parseAppUpdate(raw: unknown): MobileAppUpdate | undefined {
  if (typeof raw !== 'object' || raw === null) return undefined;
  const value = raw as Record<string, unknown>;
  if (typeof value.phase !== 'string' || !PHASES.has(value.phase)) return undefined;
  const text = (entry: unknown) => (typeof entry === 'string' && entry !== '' ? entry : undefined);
  return {
    phase: value.phase as AppUpdatePhase,
    current: typeof value.current === 'string' ? value.current : '',
    available: text(value.available),
    notes: text(value.notes),
    progress:
      typeof value.progress === 'number' && Number.isFinite(value.progress)
        ? Math.min(1, Math.max(0, value.progress))
        : undefined,
    error: text(value.error),
    running:
      typeof value.running === 'number' && value.running > 0 ? Math.floor(value.running) : 0,
  };
}

/** The body of POST /m1/app-update; `confirm` goes only when true. */
export function appUpdateBody(
  action: 'check' | 'install',
  confirm?: boolean,
): { action: 'check' | 'install'; confirm?: boolean } {
  return confirm ? { action, confirm: true } : { action };
}

export interface AppUpdateBanner {
  /** Locale key under `phone.appUpdate.`. */
  key: string;
  params: Record<string, string | number>;
  /** The button on the banner: install, or check after a failure. */
  action?: 'install' | 'check';
}

/** The banner above the session list for this state; undefined shows none (idle, up to date). */
export function appUpdateBanner(update: MobileAppUpdate | undefined): AppUpdateBanner | undefined {
  if (!update) return undefined;
  const version = update.available ?? '';
  switch (update.phase) {
    case 'available':
      return {
        key: 'phone.appUpdate.available',
        params: { version, current: update.current },
        action: 'install',
      };
    case 'ready':
      return { key: 'phone.appUpdate.ready', params: { version }, action: 'install' };
    case 'checking':
      return { key: 'phone.appUpdate.checking', params: {} };
    case 'downloading':
      return {
        key: 'phone.appUpdate.downloading',
        params: { percent: Math.round((update.progress ?? 0) * 100) },
      };
    case 'installing':
      return { key: 'phone.appUpdate.installing', params: {} };
    case 'failed':
      return { key: 'phone.appUpdate.failed', params: { error: update.error ?? '' }, action: 'check' };
    default:
      return undefined;
  }
}

/** The confirm dialog's body key and values: the running-requests text when any run. */
export function appUpdateConfirm(
  update: MobileAppUpdate,
  host: string,
): { key: string; params: Record<string, string | number> } {
  const version = update.available ?? '';
  return update.running > 0
    ? { key: 'phone.appUpdate.confirmRunning', params: { host, version, count: update.running } }
    : { key: 'phone.appUpdate.confirmIdle', params: { host, version } };
}

/** Whether the host menu offers "check for update": a settled state that is not mid-way. */
export function canCheckAppUpdate(update: MobileAppUpdate | undefined): boolean {
  return update?.phase === 'idle' || update?.phase === 'upToDate';
}
