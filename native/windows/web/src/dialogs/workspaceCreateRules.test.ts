import { describe, expect, it } from 'vitest';
import { canCreateFromGit, canMakeFolder, fill, nextName, shownProblem, type CreateCheck } from './workspaceCreateRules';

const ok = { problem: null, exists: false, locationMissing: false };

describe('workspace create helpers', () => {
  it('follows the URL until the name is edited', () => {
    expect(nextName('', false, 'MightyClaude')).toBe('MightyClaude');
    expect(nextName('mine', true, 'MightyClaude')).toBe('mine');
    expect(nextName('old', false, null)).toBe('');
  });
  it('enables 만들기 only for a valid, free target while idle', () => {
    expect(canMakeFolder(ok, false)).toBe(true);
    expect(canMakeFolder({ ...ok, problem: 'empty' }, false)).toBe(false);
    expect(canMakeFolder({ ...ok, exists: true }, false)).toBe(false);
    expect(canMakeFolder({ ...ok, locationMissing: true }, false)).toBe(false);
    expect(canMakeFolder(ok, true)).toBe(false);
    expect(canMakeFolder(null, false)).toBe(false);
  });
  it('takes a check without problem and message (the bridge leaves out nulls) as a name that can be made', () => {
    const wire = JSON.parse('{"target":"C:/a/Hello-World","exists":false,"locationMissing":false}') as CreateCheck;
    expect(canMakeFolder(wire, false)).toBe(true);
    expect(canCreateFromGit(wire, { state: 'reachable' }, false)).toBe(true);
    expect(canCreateFromGit(wire, { state: 'idle' }, false)).toBe(false);
    expect(shownProblem(wire, 'Hello-World', false)).toBeNull();
  });
  it('enables 생성 only after a reachable probe', () => {
    expect(canCreateFromGit(ok, { state: 'reachable' }, false)).toBe(true);
    expect(canCreateFromGit(ok, { state: 'checking' }, false)).toBe(false);
    expect(canCreateFromGit(ok, { state: 'failed', kind: 'auth' }, false)).toBe(false);
    expect(canCreateFromGit(ok, { state: 'reachable' }, true)).toBe(false);
  });
  it('shows the problem line only for a typed name while nothing runs', () => {
    const check = { ...ok, problem: 'badCharacter', target: 'C:\\a', message: 'bad' };
    expect(shownProblem(check, 'a:b', false)).toBe('bad');
    expect(shownProblem(check, '', false)).toBeNull();
    expect(shownProblem(check, 'a:b', true)).toBeNull();
    expect(shownProblem(null, 'a', false)).toBeNull();
  });
  it('fills placeholders', () => {
    expect(fill('받는 중 {percent}%', { percent: 42 })).toBe('받는 중 42%');
    expect(fill('{missing}', {})).toBe('{missing}');
  });
});
