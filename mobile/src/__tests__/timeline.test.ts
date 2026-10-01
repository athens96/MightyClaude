import type { MobileBlock } from '@/api/types';
import { isSettled, toneOf } from '@/lib/status-tone';
import { runTally, timelineNode } from '@/lib/timeline';

function block(status: string, id = status): MobileBlock {
  return { id, kind: 'agent', title: id, status };
}

describe('toneOf', () => {
  it('maps every contract status to its colour and anything else to the neutral', () => {
    expect(toneOf('running')).toBe('run');
    expect(toneOf('waiting')).toBe('wait');
    expect(toneOf('completed')).toBe('done');
    expect(toneOf('error')).toBe('err');
    expect(toneOf('stopped')).toBe('stop');
    expect(toneOf('idle')).toBe('idle');
    expect(toneOf('something-new')).toBe('idle');
    expect(toneOf('constructor')).toBe('idle');
  });

  it('calls completed, error and stopped settled', () => {
    expect(['completed', 'error', 'stopped'].every(isSettled)).toBe(true);
    expect(['running', 'waiting', 'idle', ''].some(isSettled)).toBe(false);
  });
});

describe('timelineNode', () => {
  it('rings the running node and lights the rail below it in blue', () => {
    expect(timelineNode(block('running'))).toEqual({ tone: 'run', ring: true, rail: 'run' });
  });

  it('lights the rail green under a finished block', () => {
    expect(timelineNode(block('completed'))).toEqual({ tone: 'done', ring: false, rail: 'done' });
  });

  it('leaves the rail an empty track under waiting, failed, stopped and unknown blocks', () => {
    expect(timelineNode(block('waiting'))).toEqual({ tone: 'wait', ring: false, rail: 'track' });
    expect(timelineNode(block('error'))).toEqual({ tone: 'err', ring: false, rail: 'track' });
    expect(timelineNode(block('stopped'))).toEqual({ tone: 'stop', ring: false, rail: 'track' });
    expect(timelineNode(block('later-word'))).toEqual({ tone: 'idle', ring: false, rail: 'track' });
  });
});

describe('runTally', () => {
  it('counts only the blocks the phone holds', () => {
    const run = {
      blocks: [
        block('completed', 'a'),
        block('error', 'b'),
        block('running', 'c'),
        block('waiting', 'd'),
        block('stopped', 'e'),
      ],
    };
    expect(runTally(run)).toEqual({ total: 5, settled: 3, running: 1 });
  });

  it('is zero for a run with no blocks yet', () => {
    expect(runTally({ blocks: [] })).toEqual({ total: 0, settled: 0, running: 0 });
  });
});
