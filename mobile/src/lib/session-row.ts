import { isAgentIOPane, type MobileSessionSummary, type Provider } from '@/api/types';
import { attentionOf, formatClock, sessionTone, type CardGlance } from '@/lib/dashboard';
import { t } from '@/lib/i18n';
import { modelLabel } from '@/lib/model-label';
import { providerMarkOutline } from '@/lib/provider-marks';
import { kindLabel, providerIsBeta, providerLabel } from '@/theme';

/**
 * What a pane row says beside and under its title (concept A, "글리프 행"). Pure, so the
 * rules — which figures show when, what the line under the meta is — are tested apart
 * from the views that draw them.
 */

/** What the pane waits on, as the amber word at a row's right: `질문 1`, else `권한 1`. */
export function waitLabel(session: MobileSessionSummary): string | undefined {
  if (session.pendingQuestions > 0) return t('phone.card.questions', { count: session.pendingQuestions });
  if (session.pendingPermissions > 0) return t('phone.card.permissions', { count: session.pendingPermissions });
  return undefined;
}

/** An agent's pane, or a pane an agent owns, names its provider; a shell or browser does not. */
function showsProvider(session: MobileSessionSummary): boolean {
  return session.kind === 'claude' || isAgentIOPane(session.kind);
}

/**
 * The provider whose brand mark goes before its name on the meta line: an agent's own
 * pane with a provider we have an outline for. A shell, a browser and an agent's own
 * terminal or browser get none, though the last two name their agent's provider too.
 */
export function rowMark(session: MobileSessionSummary): Provider | undefined {
  if (session.kind !== 'claude' || providerMarkOutline(session.provider) === undefined) return undefined;
  return session.provider;
}

/**
 * The muted line under the title: `Claude [베타] · Opus 5.5 · 2:14 · 컨텍스트 41%`, split
 * around the beta badge, which follows the provider's name. Elapsed time and context are
 * live figures, shown only while the pane runs or waits on the user; a pane at rest says
 * how long ago it settled instead (`age`, which the caller leaves out while it runs).
 */
export interface RowMeta {
  lead: string[];
  beta: boolean;
  rest: string[];
}

export function rowMeta(
  session: MobileSessionSummary,
  numbers: CardGlance | undefined,
  age: string | undefined,
): RowMeta {
  const lead: string[] = [];
  if (session.kind !== 'claude') lead.push(kindLabel(session.kind));
  if (showsProvider(session)) lead.push(providerLabel(session.provider));
  const beta = showsProvider(session) && providerIsBeta(session.provider);
  const rest: string[] = [];
  if (session.model) rest.push(rowModel(session));
  if (session.terminal && !isAgentIOPane(session.kind)) rest.push(t('phone.card.localTerminal'));
  const live = session.status === 'running' || attentionOf(session) > 0;
  if (live && numbers?.elapsedSeconds !== undefined) rest.push(formatClock(numbers.elapsedSeconds));
  if (live && numbers?.contextPercent !== undefined) {
    rest.push(t('phone.card.context', { percent: Math.round(numbers.contextPercent) }));
  }
  if (session.queued > 0) rest.push(t('phone.card.queued', { count: session.queued }));
  if (age) rest.push(age);
  return { lead, beta, rest };
}

/**
 * The pane's model with its version (`Opus 5.5`), by the rules the Mac's chip uses. The
 * CLI default keeps the Mac's name for it (`Claude 설정 따름`) and adds the model it
 * stands for when the host knows it.
 */
function rowModel(session: MobileSessionSummary): string {
  const fallback =
    session.model === 'default'
      ? t('provider.fallback.defaultLabel', { name: providerLabel(session.provider) })
      : undefined;
  return modelLabel(session.model, session.resolvedModel, fallback);
}

/**
 * The line under the meta: a running pane's last step, or why a pane stopped on an error
 * (its last line, drawn in the error ink). Every other pane has none.
 */
export type RowNote = { kind: 'step' | 'reason'; text: string };

export function rowNote(session: MobileSessionSummary): RowNote | undefined {
  const text = session.preview?.text.trim();
  if (!text) return undefined;
  if (session.status === 'running') return { kind: 'step', text };
  if (sessionTone(session) === 'err') return { kind: 'reason', text };
  return undefined;
}
