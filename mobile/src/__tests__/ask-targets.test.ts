import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { draftIsEmpty, filesTravel, parseAskTargets, shownAskTarget } from '@/lib/ask-targets';

/** The same file the Mac AskTargets, Windows Core AskTargets and Windows page read. */
const fixture = JSON.parse(
  readFileSync(resolve(__dirname, '../../../native/contracts/fixtures/ask-targets.json'), 'utf8'),
) as { targets: { id: string; label: string; executable: string }[] };

const wire = fixture.targets.map(({ id, label }) => ({ id, label }));

describe('parseAskTargets', () => {
  it('reads the fields both hosts send', () => {
    const view = parseAskTargets({ askTargets: wire, askTarget: 'antigravity' });
    expect(view.targets).toEqual(wire);
    expect(view.selected).toBe('antigravity');
  });

  it('shows the labels the shared fixture lists, in its order, with no executable', () => {
    const view = parseAskTargets({ askTargets: wire });
    expect(view.targets.map((target) => target.label)).toEqual(['Codex', 'Antigravity', 'Gemini CLI', 'Grok', 'Cursor']);
    expect(view.targets.map((target) => target.id)).toEqual(fixture.targets.map((target) => target.id));
    expect(view.targets.every((target) => Object.keys(target).sort().join() === 'id,label')).toBe(true);
    expect(view.selected).toBeNull();
  });

  it('gives no switch when the fields are missing or empty', () => {
    expect(parseAskTargets({})).toEqual({ targets: [], selected: null });
    expect(parseAskTargets(undefined)).toEqual({ targets: [], selected: null });
    expect(parseAskTargets({ askTargets: [] , askTarget: 'codex' })).toEqual({ targets: [], selected: null });
    expect(parseAskTargets({ askTargets: 'codex' })).toEqual({ targets: [], selected: null });
  });

  it('drops junk entries and repeats', () => {
    const view = parseAskTargets({
      askTargets: [null, 3, 'codex', {}, { id: 'a' }, { label: 'B' }, { id: '', label: 'x' }, { id: 1, label: 'x' }, { id: 'codex', label: 'Codex' }, { id: 'codex', label: 'Again' }],
    });
    expect(view.targets).toEqual([{ id: 'codex', label: 'Codex' }]);
  });

  it('reads a selected value outside the list as Claude', () => {
    expect(parseAskTargets({ askTargets: wire, askTarget: 'claude' }).selected).toBeNull();
    expect(parseAskTargets({ askTargets: wire, askTarget: 'nope' }).selected).toBeNull();
    expect(parseAskTargets({ askTargets: wire, askTarget: 7 }).selected).toBeNull();
  });
});

describe('shownAskTarget', () => {
  const view = parseAskTargets({ askTargets: wire, askTarget: 'codex' });

  it('shows the host choice when nothing was chosen', () => {
    expect(shownAskTarget(view, undefined, 4)).toBe('codex');
  });

  it('holds the choice until the next detail', () => {
    expect(shownAskTarget(view, { target: 'grok', revision: 4 }, 4)).toBe('grok');
    expect(shownAskTarget(view, { target: null, revision: 4 }, 4)).toBeNull();
    expect(shownAskTarget(view, { target: 'grok', revision: 4 }, 5)).toBe('codex');
  });

  it('ignores a choice the list does not offer', () => {
    expect(shownAskTarget(view, { target: 'nope', revision: 4 }, 4)).toBe('codex');
  });
});

describe('staged files while a CLI is asked', () => {
  const view = parseAskTargets({ askTargets: wire, askTarget: 'codex' });

  it('travel only to Claude, including a target that arrived with the detail', () => {
    // Chosen on the Mac or restored: no choice was made on the phone, the detail alone says codex.
    expect(filesTravel(shownAskTarget(view, undefined, 4))).toBe(false);
    expect(filesTravel(shownAskTarget(parseAskTargets({ askTargets: wire }), undefined, 4))).toBe(true);
    expect(filesTravel(shownAskTarget(view, { target: null, revision: 4 }, 4))).toBe(true);
  });

  it('are not a draft on their own while a CLI is asked', () => {
    expect(draftIsEmpty('', 2, null)).toBe(false);
    expect(draftIsEmpty('', 2, 'codex')).toBe(true);
    expect(draftIsEmpty('  ', 0, null)).toBe(true);
    expect(draftIsEmpty('look', 2, 'codex')).toBe(false);
  });
});
