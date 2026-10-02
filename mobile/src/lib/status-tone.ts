/**
 * The five colours concept D gives to state, and the one neutral. Everything that draws a
 * status — a status glyph, a stat tile, a Mighty timeline node —
 * goes through `toneOf`, so a word the host adds later lands on `idle` instead of a
 * colour it did not earn.
 */
export type Tone = 'run' | 'wait' | 'done' | 'err' | 'stop' | 'idle';

export function toneOf(status: string): Tone {
  switch (status) {
    case 'running':
      return 'run';
    case 'waiting':
      return 'wait';
    case 'completed':
      return 'done';
    case 'error':
      return 'err';
    case 'stopped':
      return 'stop';
    default:
      return 'idle';
  }
}

/** True once a block or run has finished one way or another. */
export function isSettled(status: string): boolean {
  return status === 'completed' || status === 'error' || status === 'stopped';
}
