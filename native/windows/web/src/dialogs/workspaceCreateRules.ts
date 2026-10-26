// The create sheets' own rules (dialogs/WorkspaceCreate.tsx; the Mac's WorkspaceCreateViews.swift): when 만들기 and 생성 may
// run, and how the name follows the address. Every name and location rule itself is the host's (WorkspaceCreate.cs), asked
// through `createCheck`.

/** What the host's `createCheck` says about a name in a location: the first reason it cannot be made, worded, in `message`. */
export interface CreateCheck {
  problem: string | null;
  target: string;
  exists: boolean;
  locationMissing: boolean;
  message: string | null;
}

/** The address check: idle until typing stops, then checking, then reachable or failed (with the host's words). */
export interface ProbeState { state: 'idle' | 'checking' | 'reachable' | 'failed'; kind?: string; message?: string }

/** The name field follows the URL until the user types in it. */
export function nextName(current: string, edited: boolean, suggested: string | null): string {
  return edited ? current : suggested ?? '';
}

/** 만들기 (new folder) is enabled when the host's check passes and nothing runs. */
export function canMakeFolder(check: { problem: string | null; exists: boolean; locationMissing: boolean } | null, busy: boolean): boolean {
  return !!check && check.problem === null && !check.exists && !check.locationMissing && !busy;
}

/** 생성 (from Git) also needs a reachable address. */
export function canCreateFromGit(check: { problem: string | null; exists: boolean; locationMissing: boolean } | null, probe: ProbeState, busy: boolean): boolean {
  return canMakeFolder(check, busy) && probe.state === 'reachable';
}

/** The problem line under the fields: none for an untouched empty name, none while the folder is being made (it exists then). */
export function shownProblem(check: CreateCheck | null, name: string, busy: boolean): string | null {
  return busy || name === '' || !check ? null : check.message;
}

export function fill(template: string, values: Record<string, string | number>): string {
  return template.replace(/\{(\w+)\}/g, (whole, key: string) => (key in values ? String(values[key]) : whole));
}
