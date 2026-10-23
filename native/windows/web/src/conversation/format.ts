// Small words the conversation shows: a tool's duration (ActivitySupport.DurationLabel) and an entry's local time.
import type { Translate } from '../i18n/locale';
import type { AgentActivity } from '../store/models';

const maximumDurationMs = 30 * 24 * 60 * 60 * 1000;
const terminal = new Set(['completed', 'error', 'stopped']);

/** ActivitySupport.DurationLabel: only a finished, non-turn activity with a valid duration has one. */
export function durationLabel(activity: AgentActivity, t: Translate): string | null {
  const ms = activity.durationMs;
  if (activity.kind === 'turn' || !terminal.has(activity.state) || ms === undefined || !Number.isFinite(ms) || ms < 0 || ms > maximumDurationMs) return null;
  if (ms < 1) return ms === 0 ? '0ms' : '<1ms';
  if (ms < 1000) return `${Math.trunc(ms)}ms`;
  if (ms < 60000) return t('run.activity.durationSeconds', { seconds: (Math.floor(ms / 100) / 10).toFixed(1) });
  const seconds = Math.trunc(ms / 1000);
  const minutes = String(Math.trunc(seconds / 60));
  return seconds % 60 === 0
    ? t('run.activity.durationMinutes', { minutes })
    : t('run.activity.durationMinutesSeconds', { minutes, seconds: seconds % 60 });
}

/** The live or failed state word (AgentTranscript.ToolState). */
export function toolStateKey(state: string): string {
  return state === 'waiting' ? 'transcript.tool.waiting' : state === 'error' ? 'transcript.tool.failed' : 'transcript.tool.running';
}

/** An entry's ISO time as the short local time ("오후 3:12"), empty when it does not parse. */
export function entryTime(timestamp: string, language: string): string {
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleTimeString(language, { hour: 'numeric', minute: '2-digit' });
}

/** The provider's short name (ProviderMark.Label). */
export function providerLabel(provider: string): string {
  return provider === 'codex' ? 'Codex' : provider === 'gemini' ? 'Antigravity' : 'Claude';
}

/** The text split into lines as the transcript splits them. */
export function lines(text: string): string[] {
  return text.replace(/\r\n?/g, '\n').split('\n');
}
