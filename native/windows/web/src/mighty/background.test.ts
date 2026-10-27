// The kind line a background block wears and the output lines it shows (macOS MightyGraphSupport.backgroundLabel).
import { describe, expect, it } from 'vitest';
import { translator } from '../i18n/locale';
import { backgroundLabel, shownTail } from './background';

const t = translator([{
  'graph.background.agentWaiting': 'Sub-agent · waiting',
  'graph.background.runWaiting': 'Run · by notice',
  'graph.background.agentEnded': 'Sub-agent · {status}',
  'graph.background.runEnded': 'Run · {status}',
  'graph.block.taskNotice.completed': 'completed',
  'graph.block.taskNotice.failed': 'failed',
  'graph.block.taskNotice.stopped': 'stopped',
  'styles.state.taskStatus.unknown': 'unknown',
}]);

describe('backgroundLabel', () => {
  it('says a running task waits for its notice', () => {
    expect(backgroundLabel(t, { kind: 'task', background: true, status: 'running' })).toBe('Run · by notice');
  });

  it('says how an ended task ended', () => {
    expect(backgroundLabel(t, { kind: 'task', background: true, status: 'completed' })).toBe('Run · completed');
    expect(backgroundLabel(t, { kind: 'task', background: true, status: 'error' })).toBe('Run · failed');
    expect(backgroundLabel(t, { kind: 'task', status: 'interrupted' })).toBe('Run · stopped');
    expect(backgroundLabel(t, { kind: 'task', status: 'unknown' })).toBe('Run · unknown');
  });

  it('names a background sub-agent, waiting or ended', () => {
    expect(backgroundLabel(t, { kind: 'agent', background: true, status: 'running' })).toBe('Sub-agent · waiting');
    expect(backgroundLabel(t, { kind: 'agent', background: true, status: 'cancelled' })).toBe('Sub-agent · stopped');
  });

  it('says nothing of a foreground agent, a steer or a block that is no background work', () => {
    expect(backgroundLabel(t, { kind: 'agent', status: 'running' })).toBeNull();
    expect(backgroundLabel(t, { kind: 'steer', background: true, status: 'completed' })).toBeNull();
    expect(backgroundLabel(t, { kind: 'result', status: 'completed' })).toBeNull();
  });
});

describe('shownTail', () => {
  const lines = ['1', '2', '3', '4', '5', '6'];

  it('keeps three lines while running, two once ended and all six opened', () => {
    expect(shownTail(lines, 'running', false)).toEqual(['4', '5', '6']);
    expect(shownTail(lines, 'completed', false)).toEqual(['5', '6']);
    expect(shownTail(lines, 'completed', true)).toEqual(lines);
    expect(shownTail(undefined, 'running', false)).toEqual([]);
  });
});
