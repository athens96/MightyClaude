import type { StructureStep } from '@/api/types';

/**
 * The five colours concept D gives to state, and the one neutral. Everything that draws a
 * status — a status glyph, a stat tile, a Mighty timeline node —
 * goes through `toneOf`, so a word the host adds later lands on `idle` instead of a
 * colour it did not earn. The buckets are the Mac's `DesignTone(status:)`: failed is an
 * error, cancelled and interrupted are stops.
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
    case 'failed':
      return 'err';
    case 'stopped':
    case 'cancelled':
    case 'interrupted':
      return 'stop';
    default:
      return 'idle';
  }
}

/**
 * The 파이프라인 view's colour for a card's status (the Mac's `MightyStructure.Tone.status`):
 * the block buckets, where anything still in motion runs, `done` is done, and a step still to
 * come (`pending`) takes none.
 */
export function pipelineTone(status: string): Tone | undefined {
  switch (status) {
    case 'pending':
      return undefined;
    case 'waiting':
      return 'wait';
    case 'completed':
    case 'done':
      return 'done';
    case 'error':
    case 'failed':
      return 'err';
    case 'stopped':
    case 'cancelled':
    case 'interrupted':
      return 'stop';
    default:
      return 'run';
  }
}

/**
 * A step's colour (`MightyStructure.Tone.step`): done, error and stopped in their own, the
 * current step running, or waiting while its request waits; a step still to come none.
 */
export function stepTone(step: StructureStep, waiting = false): Tone | undefined {
  switch (step) {
    case 'done':
      return 'done';
    case 'current':
      return waiting ? 'wait' : 'run';
    case 'error':
      return 'err';
    case 'stopped':
      return 'stop';
    default:
      return undefined;
  }
}

/** True once a block or run has finished one way or another. */
export function isSettled(status: string): boolean {
  return status === 'completed' || status === 'error' || status === 'stopped';
}
