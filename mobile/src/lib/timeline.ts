import type { MobileBlock, MobileMightyRun } from '@/api/types';
import { isSettled, toneOf, type Tone } from '@/lib/status-tone';

/**
 * The Mighty block list drawn as a vertical timeline: a rail, one round node per block
 * coloured by the block's status, and a spreading ring on the node that is still running.
 * Pure, so the mapping is tested apart from the drawing.
 */
export interface TimelineNode {
  tone: Tone;
  /** The running node pulses a ring outwards. */
  ring: boolean;
  /**
   * The rail below the node: lit in the node's own colour once the block finished well
   * or while it runs, left as an empty track otherwise.
   */
  rail: Tone | 'track';
}

export function timelineNode(block: Pick<MobileBlock, 'status'>): TimelineNode {
  const tone = toneOf(block.status);
  const rail = block.status === 'completed' || block.status === 'running' ? tone : 'track';
  return { tone, ring: block.status === 'running', rail };
}

/**
 * Counted only from the blocks the phone holds. `total` is the blocks present, not the
 * blocks a run will end up with — the host never says how many are to come — so the
 * screen words it as "블록 n개 · 끝남 m", never as "m / n" progress.
 */
export interface RunTally {
  total: number;
  settled: number;
  running: number;
}

export function runTally(run: Pick<MobileMightyRun, 'blocks'>): RunTally {
  let settled = 0;
  let running = 0;
  for (const block of run.blocks) {
    if (isSettled(block.status)) settled += 1;
    else if (block.status === 'running') running += 1;
  }
  return { total: run.blocks.length, settled, running };
}
