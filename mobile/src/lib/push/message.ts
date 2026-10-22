import { utf8Decode } from '@/api/relay/crypto';

export const PUSH_KINDS = ['completed', 'failed', 'permission', 'question', 'plan', 'signin'] as const;
export type PushKind = (typeof PUSH_KINDS)[number];
export const PUSH_BUTTONS = ['claude', 'antigravity', 'antigravityOnce', 'none'] as const;
export type PushButtons = (typeof PUSH_BUTTONS)[number];

/** The plaintext a host seals (native/macos/Sources/MightyCore/Remote/MobilePush.swift MobilePushMessage). */
export interface PushMessage {
  v: 1;
  id: string;
  kind: PushKind;
  hostId: string;
  hostName: string;
  workspace: string;
  title: string;
  summary: string;
  sessionId: string;
  permission?: { requestId: string; runId: string; buttons: PushButtons };
  at: string;
}

type TextField = 'id' | 'hostId' | 'hostName' | 'workspace' | 'title' | 'summary' | 'sessionId' | 'at';

/** Longest value in characters (Unicode scalars, as the host clips: 80 for names and titles, 200 for the summary). */
const LIMITS: Record<TextField, number> = {
  id: 300, hostId: 128, hostName: 80, workspace: 80, title: 80, summary: 200, sessionId: 128, at: 40,
};

function text(value: unknown, limit: number): value is string {
  return typeof value === 'string' && [...value].length <= limit;
}

function record(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/**
 * The message, or null for anything the phone cannot show safely: another version, an unknown
 * kind or buttons, a missing, mistyped or too long field, an empty id or session, or a permission
 * block on anything but a permission (and a permission without one). Such a push is dropped.
 */
export function parsePushMessage(bytes: Uint8Array): PushMessage | null {
  let value: unknown;
  try {
    value = JSON.parse(utf8Decode(bytes));
  } catch {
    return null;
  }
  const m = record(value);
  if (!m || m['v'] !== 1 || !PUSH_KINDS.includes(m['kind'] as PushKind)) return null;
  for (const [key, limit] of Object.entries(LIMITS)) if (!text(m[key], limit)) return null;
  if (m['id'] === '' || m['sessionId'] === '') return null;
  const message: PushMessage = {
    v: 1,
    id: m['id'] as string,
    kind: m['kind'] as PushKind,
    hostId: m['hostId'] as string,
    hostName: m['hostName'] as string,
    workspace: m['workspace'] as string,
    title: m['title'] as string,
    summary: m['summary'] as string,
    sessionId: m['sessionId'] as string,
    at: m['at'] as string,
  };
  const hasPermission = 'permission' in m;
  if (hasPermission !== (message.kind === 'permission')) return null;
  if (hasPermission) {
    const p = record(m['permission']);
    if (!p || !text(p['requestId'], 128) || !text(p['runId'], 128) || !PUSH_BUTTONS.includes(p['buttons'] as PushButtons)) return null;
    message.permission = { requestId: p['requestId'], runId: p['runId'], buttons: p['buttons'] as PushButtons };
  }
  return message;
}
