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
 * The goldens are read with `fs`, not `import`: `mobile/tsconfig.json` is rooted at
 * `mobile/` and has no `resolveJsonModule`, so importing them would fail `tsc --noEmit`.
 */

/** Repo root, three levels above `mobile/src/__tests__`. */
const GOLDEN_DIR = join(__dirname, '..', '..', '..', 'styles', 'golden');

interface Golden {
  name: string;
  payload: unknown;
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
    payload !== null && typeof payload === 'object' && !Array.isArray(payload)
      ? Object.entries(payload as Record<string, unknown>).map(([panelCase, value]) => ({ name, panelCase, payload: value }))
      : [{ name, panelCase: '', payload: undefined }],
  );
}

describe('recorded style panels', () => {
  it('holds only files this app can read', () => {
    // Nothing recorded yet is a pass: the engine tags before the first manifest lands.
    for (const golden of goldens()) {
      expect(typeof golden.payload).toBe('object');
      expect(golden.payload).not.toBeNull();
    }
  });

  it('passes every golden through the normaliser and the view model', () => {
    for (const { name, panelCase, payload } of recordedPanels()) {
      const panel = normalizeStylePanel(payload);
      // The file and case names ride in the assertion so a failure names what broke.
      expect({ name, panelCase, readable: panel !== undefined }).toEqual({ name, panelCase, readable: true });
      if (!panel) continue;

      expect(panel.style.id.length).toBeGreaterThan(0);
      expect(panel.presentation.headerTitle.length).toBeGreaterThan(0);

      // The default group, then each one the map offers.
      const groupIds = [undefined, ...panel.groups.map((group) => group.id)];
      for (const groupId of groupIds) {
        const model = styleViewModel(panel, groupId);
        expect(model.headerTitle).toBe(panel.presentation.headerTitle);
        // Only a bundled style goes unbadged, wherever its name appears.
        expect(model.sourceBadge === undefined).toBe(panel.presentation.source === 'bundled');
        for (const view of model.actions) {
          expect(panel.actions).toContain(view.action);
        }
      }

      // Every action in the catalogue can be posted, and text follows `takesText`.
      for (const action of panel.actions) {
        const request = guidedRequestFor(panel, action.id, '대상');
        expect(request.styleId).toBe(panel.style.id);
        expect(request.actionId).toBe(action.id);
        expect(request.text === undefined).toBe(!action.takesText);
      }
    }
  });
});
