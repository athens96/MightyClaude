import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { guidedRequestFor, normalizeStylePanel, styleViewModel } from '@/lib/styles';

/**
 * The phone half of the frozen golden contract (contract 8.4). The engine records
 * `styles/golden/<id>.panel.json` from the same projection it sends over the wire, so
 * every golden that exists must pass through this app's normaliser and view model. A
 * manifest added after the tag brings its golden with it and this file — frozen before
 * the tag — checks it without a line of engine or phone code changing.
 *
 * What it asserts is what the generic renderer actually depends on: that each recorded
 * case carries a catalogue, that the chips drawn are exactly the selected group's ids
 * (or the flat `next` when there is no map), that at most one chip is filled and by the
 * documented rule, and that an empty composer sends no `text`. The count and the names
 * of the cases are the engine's business, so this iterates whatever it finds — but a
 * file recording nothing is a failure, not a pass, or deleting the cases would silence
 * the contract.
 *
 * The goldens are read with `fs`, not `import`: `mobile/tsconfig.json` is rooted at
 * `mobile/` and has no `resolveJsonModule`, so importing them would fail `tsc --noEmit`.
 */

/** Repo root, three levels above `mobile/src/__tests__`. */
const GOLDEN_DIR = join(__dirname, '..', '..', '..', 'styles', 'golden');

interface Golden {
  name: string;
  payload: unknown;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** Every recorded golden. Zero of them is the valid answer before the first manifest. */
function goldens(): Golden[] {
  let names: string[];
  try {
    names = readdirSync(GOLDEN_DIR);
  } catch {
    return [];
  }
  return names
    .filter((name) => name.endsWith('.panel.json'))
    .sort()
    .map((name) => ({
      name,
      payload: JSON.parse(readFileSync(join(GOLDEN_DIR, name), 'utf8')) as unknown,
    }));
}

// A golden holds one panel per fixed engine input (docs/mighty-styles.md 8.4), keyed by case name.
function recordedPanels(): { name: string; panelCase: string; payload: unknown }[] {
  return goldens().flatMap(({ name, payload }) =>
    isRecord(payload)
      ? Object.entries(payload).map(([panelCase, value]) => ({ name, panelCase, payload: value }))
      : [{ name, panelCase: '', payload: undefined }],
  );
}

describe('recorded style panels', () => {
  it('holds only files this app can read, each recording at least one case', () => {
    // Nothing recorded yet is a pass: the engine tags before the first manifest lands.
    for (const { name, payload } of goldens()) {
      expect({ name, object: isRecord(payload) }).toEqual({ name, object: true });
      const cases = isRecord(payload) ? Object.keys(payload) : [];
      // `{}` — or a file whose cases were all deleted — records nothing, so it would
      // make every loop below vacuous and the whole contract green by omission.
      expect({ name, cases: cases.length > 0 }).toEqual({ name, cases: true });
    }
    // The same guard one level up: goldens on disk must produce panels to check.
    if (goldens().length > 0) expect(recordedPanels().length).toBeGreaterThan(0);
  });

  it('records at least one case per style that actually offers a chip', () => {
    // A style may send an empty `next` for a single case — a run in flight, a phase it
    // asks about rather than offers steps for. All of them empty is a dropped `next`,
    // and every loop in the test below would then pass on nothing at all.
    for (const { name } of goldens()) {
      const drawn = recordedPanels()
        .filter((recorded) => recorded.name === name)
        .map(({ payload }) => normalizeStylePanel(payload))
        .filter((panel) => panel !== undefined)
        .some((panel) => styleViewModel(panel).actions.length > 0);
      expect({ name, drawn }).toEqual({ name, drawn: true });
    }
  });

  it('passes every golden through the normaliser and the view model', () => {
    for (const { name, panelCase, payload } of recordedPanels()) {
      const where = { name, panelCase };
      const panel = normalizeStylePanel(payload);
      // The file and case names ride in the assertion so a failure names what broke.
      expect({ ...where, readable: panel !== undefined }).toEqual({ ...where, readable: true });
      if (!panel) continue;

      expect(panel.style.id.length).toBeGreaterThan(0);
      expect(panel.presentation.headerTitle.length).toBeGreaterThan(0);
      // A case with no catalogue would make every assertion below vacuous, and there is
      // nothing for the panel to draw or for "더 보기" to reach.
      expect({ ...where, catalogue: panel.actions.length > 0 }).toEqual({
        ...where,
        catalogue: true,
      });

      // The default group, then each one the map offers.
      const groupIds = [undefined, ...panel.groups.map((group) => group.id)];
      for (const groupId of groupIds) {
        const at = { ...where, groupId };
        const model = styleViewModel(panel, groupId);
        expect(model.headerTitle).toBe(panel.presentation.headerTitle);
        // Only a bundled style goes unbadged, wherever its name appears.
        expect(model.sourceBadge === undefined).toBe(panel.presentation.source === 'bundled');

        // The chips are exactly the selected group's ids, or the flat `next` when the
        // groups are not a map. `next` may be empty — a style with a run in flight sends
        // no step — and the catalogue stays reachable through `rest` either way.
        const expected = model.showMap
          ? (panel.groups.find((group) => group.id === model.selectedGroupId)?.actions ?? [])
          : panel.next;
        expect({ ...at, shown: model.actions.map((view) => view.action.id) }).toEqual({
          ...at,
          shown: expected,
        });
        const drawn = new Set(model.actions.map((view) => view.action.id));
        expect(model.rest.map((action) => action.id)).toEqual(
          panel.actions.filter((action) => !drawn.has(action.id)).map((action) => action.id),
        );

        // At most one filled chip, and by the one rule: none while a map is on screen,
        // otherwise the action the host marked or else the head of the list.
        const filled = model.actions.filter((view) => view.prominent).map((view) => view.action.id);
        const head = model.actions.find((view) => view.action.prominent) ?? model.actions[0];
        expect({ ...at, filled }).toEqual({
          ...at,
          filled: model.showMap || !head ? [] : [head.action.id],
        });
      }

      // Every action in the catalogue can be posted, and text follows `takesText`.
      for (const action of panel.actions) {
        const request = guidedRequestFor(panel, action.id, '대상');
        expect(request.styleId).toBe(panel.style.id);
        expect(request.actionId).toBe(action.id);
        expect(request.text === undefined).toBe(!action.takesText);
        // An empty composer sends the bare action, whatever the action says it takes:
        // the rule `/guided` relies on, and `requiresText` is a UI hint only (1.3.3).
        expect({ ...where, id: action.id, bare: guidedRequestFor(panel, action.id, '   ').text })
          .toEqual({ ...where, id: action.id, bare: undefined });
      }
    }
  });
});
