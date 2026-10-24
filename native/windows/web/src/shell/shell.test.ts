import { describe, expect, it } from 'vitest';
import { filterWorkspaces } from './filter';
import { shortcutAction } from './shortcuts';

const key = (code: string, extra: Partial<{ key: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean }> = {}) =>
  ({ key: extra.key ?? '', code, ctrlKey: extra.ctrlKey ?? true, shiftKey: extra.shiftKey ?? false, altKey: extra.altKey ?? false, metaKey: false });

describe('sidebar search (AddPaneMenu.Filtered)', () => {
  const list = [{ name: 'Alpha', path: 'C:\\work\\alpha' }, { name: 'Beta', path: 'D:\\Projects\\b' }];
  it('matches name or path, case-insensitively, trimmed', () => {
    expect(filterWorkspaces(list, '  ALP ').map((w) => w.name)).toEqual(['Alpha']);
    expect(filterWorkspaces(list, 'projects').map((w) => w.name)).toEqual(['Beta']);
    expect(filterWorkspaces(list, '   ')).toHaveLength(2);
    expect(filterWorkspaces(list, 'zzz')).toHaveLength(0);
  });
});

describe('shortcuts (spec section 8)', () => {
  it('maps the window keys', () => {
    expect(shortcutAction(key('KeyO'))).toBe('openFolder');
    expect(shortcutAction(key('KeyN'))).toBe('newPane');
    expect(shortcutAction(key('KeyK'))).toBe('search');
    expect(shortcutAction(key('KeyB'))).toBe('toggleSidebar');
    expect(shortcutAction(key('KeyT'))).toBe('newShell');
    expect(shortcutAction(key('Comma'))).toBe('settings');
    expect(shortcutAction(key('KeyE', { shiftKey: true }))).toBe('files');
    expect(shortcutAction(key('F1', { key: 'F1', ctrlKey: false }))).toBe('help');
    expect(shortcutAction(key('Equal'))).toBe('textLarger');
    expect(shortcutAction(key('Minus'))).toBe('textSmaller');
    expect(shortcutAction(key('Digit0'))).toBe('textReset');
  });
  it('leaves other keys alone', () => {
    expect(shortcutAction(key('KeyA'))).toBeNull();
    expect(shortcutAction(key('KeyO', { ctrlKey: false }))).toBeNull();
    expect(shortcutAction(key('KeyO', { altKey: true }))).toBeNull();
  });
});
