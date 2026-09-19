import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { guidedRequestFor, normalizeStylePanel, styleViewModel } from '@/lib/styles';

/**
 * The phone half of `styles/gstack.json`, read from the bytes the engine
 * recorded and the Mac checks in `StylesThirdPartyGstackTests`. The opposite
 * shape from oh-my-claudecode: glyphs instead of icons, a scope line on every
 * action, and nothing auto-allowed.
 *
 * Read with `fs` rather than `import`, because `mobile/tsconfig.json` has no
 * `resolveJsonModule` and is rooted at `mobile/` (contract 8.4).
 */
const GOLDEN = join(__dirname, '..', '..', '..', 'styles', 'golden', 'gstack.panel.json');
const recorded = JSON.parse(readFileSync(GOLDEN, 'utf8')) as Record<string, unknown>;

/** The catalogue in manifest order; every id must survive the phone's id filter. */
const ACTION_IDS = [
  'office-hours',
  'spec',
  'autoplan',
  'plan-ceo-review',
  'plan-eng-review',
  'plan-design-review',
  'plan-devex-review',
  'investigate',
  'codex',
  'health',
  'qa',
  'qa-only',
  'review',
  'design-review',
  'devex-review',
  'cso',
  'benchmark',
  'ship',
  'land-and-deploy',
  'canary',
  'document-release',
  'retro',
  'learn',
  'context-save',
  'context-restore',
  'freeze',
  'unfreeze',
];

function panelFor(recordedCase: string) {
  const panel = normalizeStylePanel(recorded[recordedCase]);
  if (!panel) throw new Error(`골든 ${recordedCase} 케이스를 읽지 못했습니다`);
  return panel;
}

describe('gstack 골든', () => {
  it('names the style and badges it, because the source is user', () => {
    const panel = panelFor('empty');
    expect(panel.style.id).toBe('gstack');
    expect(panel.style.name).toBe('gstack');
    expect(panel.style.source).toBe('user');
    expect(styleViewModel(panel).sourceBadge).toBe('사용자 등록');
    expect(styleViewModel(panel).tint).toBe('orange');
    expect(panel.presentation.headerTitle).toBe('gstack · 계획');
  });

  it('carries the whole catalogue with a glyph each, and every id survives the id filter', () => {
    for (const recordedCase of Object.keys(recorded)) {
      expect(panelFor(recordedCase).actions.map((action) => action.id)).toEqual(ACTION_IDS);
    }
    const panel = panelFor('empty');
    expect(panel.actions).toHaveLength(27);
    // A glyph is what the phone draws in place of an SF Symbol, and what keeps
    // `/qa` and `/qa-only` apart in a request block (contract 1.10).
    const glyphs = panel.actions.map((action) => action.glyph);
    expect(glyphs.filter((glyph) => glyph !== undefined)).toHaveLength(27);
    expect(new Set(glyphs).size).toBe(27);
    expect(panel.actions.map((action) => action.scope).filter((scope) => scope !== undefined)).toHaveLength(27);
    expect(panel.actions.every((action) => action.takesText)).toBe(true);
    expect(panel.actions.filter((action) => action.flags.includes('userInvoked')).map((a) => a.id)).toEqual([
      'plan-ceo-review',
      'plan-eng-review',
      'plan-design-review',
      'plan-devex-review',
    ]);
    expect(panel.actions.filter((action) => action.flags.includes('readOnly')).map((a) => a.id)).toEqual([
      'qa-only',
      'context-restore',
    ]);
  });

  it('draws the plan-stage chips, with one filled head and the rest reachable', () => {
    const empty = panelFor('empty');
    expect(empty.phase).toEqual({ id: 'plan', title: '계획', index: 0, count: 5 });
    const model = styleViewModel(empty);
    // One group and no axis, so no map to pick from and the flat `next` is drawn.
    expect(model.showMap).toBe(false);
    expect(model.selectedGroupId).toBeUndefined();
    expect(model.actions.map((view) => view.action.id)).toEqual([
      'office-hours',
      'spec',
      'autoplan',
      'plan-eng-review',
      'investigate',
    ]);
    expect(model.actions.filter((view) => view.prominent).map((view) => view.action.id)).toEqual(['office-hours']);
    expect(model.rest).toHaveLength(22);
    expect(model.actions.some((view) => view.recommended)).toBe(false);
    expect(model.guidance).toBe('계획 단계입니다. 대상을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.');
    // gstack's first catalogue action already sits in the default stage, so the
    // recorded `afterFirstAction` lands on the same phase as `empty`; the stage
    // walk itself is asserted on the Mac, where prompts can be fed in.
    expect(panelFor('afterFirstAction').phase).toEqual(empty.phase);
  });

  it('shows the setup block only while the Mac says the skills are missing', () => {
    expect(styleViewModel(panelFor('empty')).setup).toBeUndefined();
    const notReady = styleViewModel(panelFor('notReady')).setup;
    expect(notReady?.missing).toEqual(['gstack 스킬이 설치되어 있지 않습니다']);
    expect(notReady?.hint).toContain('Enter는 직접 누르세요.');
    expect(notReady?.installCommand).toBe(
      'git clone --single-branch --depth 1 https://github.com/garrytan/gstack.git ~/.claude/skills/gstack && cd ~/.claude/skills/gstack && ./setup',
    );
  });

  it('posts a guided request that carries the composer text, and drops an empty one', () => {
    const panel = panelFor('empty');
    expect(guidedRequestFor(panel, 'qa', '  https://staging.example.dev  ')).toEqual({
      styleId: 'gstack',
      actionId: 'qa',
      text: 'https://staging.example.dev',
    });
    expect(guidedRequestFor(panel, 'qa', '   ')).toEqual({
      styleId: 'gstack',
      actionId: 'qa',
      text: undefined,
    });
    for (const action of panel.actions) {
      expect(guidedRequestFor(panel, action.id, '대상').text).toBe('대상');
    }
  });
});
