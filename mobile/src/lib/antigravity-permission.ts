import { describeError, type MobileClient } from '@/api/client';
import type { MobileAntigravityAction, MobileAntigravityKind, MobilePermission } from '@/api/types';
import { t } from '@/lib/i18n';

/** The `toolName` of an Antigravity card (docs/mobile-remote.md "Antigravity 권한 카드"). */
export const ANTIGRAVITY_PERMISSION_TOOL = 'AntigravityPermission';

/** The card's three buttons: 이번만 허용, 항상 허용, 거절. */
export type AntigravityDecision = 'once' | 'always' | 'deny';

const KINDS: readonly MobileAntigravityKind[] = ['command', 'url', 'file', 'other'];

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function actionFrom(value: unknown): MobileAntigravityAction | undefined {
  if (value === null || typeof value !== 'object') return undefined;
  const raw = value as Record<string, unknown>;
  const action = text(raw.action);
  const displayName = text(raw.displayName) ?? action;
  if (action === undefined || displayName === undefined) return undefined;
  const kind = KINDS.find((known) => known === raw.kind) ?? 'other';
  const target = text(raw.target);
  const rule = text(raw.rule);
  return {
    kind,
    action,
    displayName,
    ...(target !== undefined ? { target } : {}),
    ...(rule !== undefined ? { rule } : {}),
  };
}

/**
 * The refused actions of an Antigravity card, or undefined for any other request. A card
 * whose `antigravity` the phone cannot read is drawn as an ordinary permission card (its
 * title, fields and allow/deny), as an older phone draws it.
 */
export function antigravityActions(permission: MobilePermission): MobileAntigravityAction[] | undefined {
  if (permission.toolName !== ANTIGRAVITY_PERMISSION_TOOL) return undefined;
  const card: unknown = permission.antigravity;
  if (card === null || typeof card !== 'object') return undefined;
  const list: unknown = (card as { actions?: unknown }).actions;
  if (!Array.isArray(list)) return undefined;
  const actions = list.map(actionFrom).filter((action): action is MobileAntigravityAction => action !== undefined);
  return actions.length > 0 ? actions : undefined;
}

/** "명령", "주소", "파일", "작업". */
export function antigravityKindLabel(kind: MobileAntigravityKind): string {
  switch (kind) {
    case 'command':
      return t('antigravity.permission.kind.command');
    case 'url':
      return t('antigravity.permission.kind.url');
    case 'file':
      return t('antigravity.permission.kind.file');
    case 'other':
      return t('antigravity.permission.kind.other');
  }
}

/** The body of `POST …/permission` for one button; `always` goes with 항상 허용 only. */
export function antigravityAnswerBody(
  permission: Pick<MobilePermission, 'id' | 'runId'>,
  decision: AntigravityDecision,
): { requestId: string; runId: string; allow: boolean; always?: boolean } {
  const body = { requestId: permission.id, runId: permission.runId };
  switch (decision) {
    case 'once':
      return { ...body, allow: true };
    case 'always':
      return { ...body, allow: true, always: true };
    case 'deny':
      return { ...body, allow: false };
  }
}

/** What the screen says once the host took the answer. */
export function antigravityAnsweredToast(decision: AntigravityDecision): string {
  return decision === 'deny' ? t('phone.session.toast.denied') : t('phone.session.toast.allowed');
}

/**
 * Sends one button's answer. A refusal (a 409 with the host's reason: the settings file
 * could not be changed, no rule can allow it, the card is gone) comes back as `reason`;
 * the screen keeps the card and shows it there, as the Mac does.
 */
export async function sendAntigravityAnswer(
  client: Pick<MobileClient, 'respondPermission'>,
  sessionId: string,
  permission: Pick<MobilePermission, 'id' | 'runId'>,
  decision: AntigravityDecision,
): Promise<{ ok: true } | { ok: false; reason: string }> {
  try {
    await client.respondPermission(sessionId, antigravityAnswerBody(permission, decision));
    return { ok: true };
  } catch (error) {
    return { ok: false, reason: describeError(error) };
  }
}
