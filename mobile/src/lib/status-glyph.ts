import { t } from '@/lib/i18n';
import { toneOf, type Tone } from '@/lib/status-tone';

/**
 * The small mark before a pane's title (concept A, "글리프 행"): a turning spark while it
 * runs, an amber "?" disc while it waits on the user, a check when done, a slashed ring
 * when stopped, a red "!" disc on error. An idle pane wears its own glyph instead — a
 * terminal for a shell, a globe for a browser, a quiet ring for an agent — and so does a
 * status word the host adds later, rather than a state colour it did not earn.
 */
export type GlyphShape =
  | 'spark'
  | 'question'
  | 'check'
  | 'slashedRing'
  | 'alert'
  | 'terminal'
  | 'globe'
  | 'ring';

export interface StatusGlyphSpec {
  shape: GlyphShape;
  tone: Tone;
}

function idleShape(kind: string | undefined): GlyphShape {
  if (kind === 'shell' || kind === 'agent-terminal') return 'terminal';
  if (kind === 'browser' || kind === 'agent-browser') return 'globe';
  return 'ring';
}

/** `status` is what a row draws (`displayStatus`): a pane's own status, or `waiting`. */
export function statusGlyphFor(status: string, kind?: string): StatusGlyphSpec {
  const tone = toneOf(status);
  switch (tone) {
    case 'run':
      return { shape: 'spark', tone };
    case 'wait':
      return { shape: 'question', tone };
    case 'done':
      return { shape: 'check', tone };
    case 'err':
      return { shape: 'alert', tone };
    case 'stop':
      return { shape: 'slashedRing', tone };
    case 'idle':
      return { shape: idleShape(kind), tone };
  }
}

const WORD_KEYS: Record<string, string> = {
  running: 'session.state.running',
  waiting: 'phone.dashboard.stat.waiting',
  completed: 'session.state.completed',
  error: 'session.state.error',
  stopped: 'session.state.stopped',
  idle: 'session.state.idle',
};

/**
 * The status word a glyph stands for — its accessibility label and the header's word.
 * A word the contract does not list is shown as the host sent it; an empty one reads idle.
 */
export function statusWord(status: string): string {
  const key = Object.hasOwn(WORD_KEYS, status) ? WORD_KEYS[status] : undefined;
  if (key) return t(key);
  return status.length > 0 ? status : t('session.state.idle');
}
