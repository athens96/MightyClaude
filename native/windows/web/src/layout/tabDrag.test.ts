import { describe, expect, it } from 'vitest';
import { dragSlack } from './geometry';
import { dockArguments, resolveTabDrop, startTabPress, stepTabPress, stripScroll, stripScrollEdge, stripScrollStep, type TabDragHit, type TabDragTarget, type TabPress } from './tabDrag';

const group: TabDragHit = { kind: 'group', workspaceId: 'w', groupId: 'g', box: { left: 100, top: 50, width: 400, height: 300 } };
const tab: TabDragHit = { kind: 'tab', workspaceId: 'w', groupId: 'g', tabId: 't2', index: 2, box: { left: 200, top: 52, width: 80, height: 24 } };
const strip: TabDragHit = { kind: 'strip', workspaceId: 'w', groupId: 'g' };

describe('tab drag target (pointer to group edge or strip index)', () => {
  it('over a group: its drop zone, in the group box', () => {
    expect(resolveTabDrop(group, 105, 60, 'w')).toEqual({ groupId: 'g', edge: 'center' });
    expect(resolveTabDrop(group, 120, 200, 'w')).toEqual({ groupId: 'g', edge: 'left' });
    expect(resolveTabDrop(group, 490, 200, 'w')).toEqual({ groupId: 'g', edge: 'right' });
    expect(resolveTabDrop(group, 300, 110, 'w')).toEqual({ groupId: 'g', edge: 'top' });
    expect(resolveTabDrop(group, 300, 340, 'w')).toEqual({ groupId: 'g', edge: 'bottom' });
    expect(resolveTabDrop(group, 300, 200, 'w')).toEqual({ groupId: 'g', edge: 'center' });
  });
  it('over a tab: a merge before or after it by the half under the pointer', () => {
    expect(resolveTabDrop(tab, 210, 60, 'w')).toEqual({ groupId: 'g', edge: 'center', index: 2, tabId: 't2', side: 'left' });
    expect(resolveTabDrop(tab, 270, 60, 'w')).toEqual({ groupId: 'g', edge: 'center', index: 3, tabId: 't2', side: 'right' });
  });
  it('over a strip gap or a close button: a merge at the end, never a split', () => {
    expect(resolveTabDrop(strip, 105, 60, 'w')).toEqual({ groupId: 'g', edge: 'center' });
    expect(resolveTabDrop(strip, 495, 60, 'w')).toEqual({ groupId: 'g', edge: 'center' });
  });
  it('nothing under the pointer, or another workspace, is no target', () => {
    expect(resolveTabDrop(null, 0, 0, 'w')).toBeNull();
    expect(resolveTabDrop(group, 300, 200, 'other')).toBeNull();
    expect(resolveTabDrop(tab, 210, 60, 'other')).toBeNull();
    expect(resolveTabDrop(strip, 210, 60, 'other')).toBeNull();
  });
  it('the host call carries the index only for a landing on a tab', () => {
    expect(dockArguments('s', 'w', { groupId: 'g', edge: 'left' })).toEqual({ sessionId: 's', workspaceId: 'w', groupId: 'g', edge: 'left' });
    expect(dockArguments('s', 'w', { groupId: 'g', edge: 'center', index: 0, tabId: 't', side: 'left' }))
      .toEqual({ sessionId: 's', workspaceId: 'w', groupId: 'g', edge: 'center', index: 0 });
  });
});

describe('tab strip scroll while dragging', () => {
  it('near either end of a strip it scrolls toward that end, elsewhere not', () => {
    expect(stripScroll(100 + stripScrollEdge - 1, 100, 400)).toBe(-stripScrollStep);
    expect(stripScroll(400 - stripScrollEdge + 1, 100, 400)).toBe(stripScrollStep);
    expect(stripScroll(250, 100, 400)).toBe(0);
    expect(stripScroll(100 + stripScrollEdge, 100, 400)).toBe(0);
  });
  it('a strip too narrow for both ends does not scroll', () => {
    expect(stripScroll(101, 100, 100 + 2 * stripScrollEdge)).toBe(0);
  });
});

describe('tab press (slack, drop, cancel)', () => {
  const target = { groupId: 'g', edge: 'right' as const };
  const moved = (press: TabPress, x: number, y: number, to: TabDragTarget | null = target) => {
    const step = stepTabPress(press, { type: 'move', x, y, buttons: 1, target: to });
    if (!step.press) throw new Error('the press ended on a move');
    return step.press;
  };
  it('a press within the slack stays a click and keeps no target', () => {
    const press = moved(startTabPress(10, 10), 10 + dragSlack - 1, 10 - (dragSlack - 1));
    expect(press.phase).toBe('pressed');
    expect(press.target).toBeNull();
    expect(stepTabPress(press, { type: 'up', target })).toEqual({ press: null, end: 'click' });
  });
  it('past the slack on either axis it drags, and stays a drag back inside it', () => {
    const across = moved(startTabPress(10, 10), 10 + dragSlack, 10);
    expect(across.phase).toBe('dragging');
    expect(moved(startTabPress(10, 10), 10, 10 - dragSlack).phase).toBe('dragging');
    expect(moved(across, 10, 10).phase).toBe('dragging');
  });
  it('a release lands on the target under the release point, not the last move\'s', () => {
    const press = moved(startTabPress(0, 0), 40, 0);
    const other = { groupId: 'h', edge: 'center' as const, index: 1 };
    expect(stepTabPress(press, { type: 'up', target: other })).toEqual({ press: null, end: 'drop', target: other });
  });
  it('a release over no target, Esc or a cancelled pointer cancels', () => {
    const press = moved(startTabPress(0, 0), 40, 0);
    expect(stepTabPress(press, { type: 'up', target: null })).toEqual({ press: null, end: 'cancel' });
    expect(stepTabPress(press, { type: 'cancel' })).toEqual({ press: null, end: 'cancel' });
    expect(stepTabPress(startTabPress(0, 0), { type: 'cancel' })).toEqual({ press: null, end: 'cancel' });
  });
  it('a move without the primary button held (its release went unheard) cancels instead of waiting for a later click', () => {
    const press = moved(startTabPress(0, 0), 40, 0);
    expect(stepTabPress(press, { type: 'move', x: 60, y: 0, buttons: 0, target })).toEqual({ press: null, end: 'cancel' });
    expect(stepTabPress(press, { type: 'move', x: 60, y: 0, buttons: 2, target })).toEqual({ press: null, end: 'cancel' });
    expect(stepTabPress(startTabPress(0, 0), { type: 'move', x: 1, y: 0, buttons: 0, target })).toEqual({ press: null, end: 'cancel' });
    expect(stepTabPress(press, { type: 'move', x: 60, y: 0, buttons: 3, target }).press?.phase).toBe('dragging');
  });
});
