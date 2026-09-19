import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { guidedRequestFor, normalizeStylePanel, styleViewModel } from '@/lib/styles';

/**
 * The phone half of `styles/oh-my-claudecode.json`. It reads the very bytes the
 * engine recorded — the same file `StylesThirdPartyOhMyClaudecodeTests` checks
 * on the Mac — so if the projection and this renderer ever disagree by one line,
 * one of the two sides fails. Adding this style cost no engine and no phone code.
 *
 * Read with `fs` rather than `import`: `mobile/tsconfig.json` is rooted at
 * `mobile/` and has no `resolveJsonModule`, so importing it would fail
 * `tsc --noEmit` (contract 8.4).
 */
const GOLDEN = join(__dirname, '..', '..', '..', 'styles', 'golden', 'oh-my-claudecode.panel.json');
const recorded = JSON.parse(readFileSync(GOLDEN, 'utf8')) as Record<string, unknown>;

/** The catalogue in manifest order; every id must survive the phone's id filter. */
const ACTION_IDS = [
  'plan',
  'ralplan',
  'deep-interview',
  'execute',
  'autopilot',
  'ralph',
  'team',
  'review',
  'verify',
  'research',
  'external-context',
  'trace',
  'debug',
  'wiki',
  'remember',
  'cancel',
];

function panelFor(recordedCase: string) {
  const panel = normalizeStylePanel(recorded[recordedCase]);
  if (!panel) throw new Error(`골든 ${recordedCase} 케이스를 읽지 못했습니다`);
  return panel;
}

describe('oh-my-claudecode 골든', () => {
  it('names the style and badges it, because the source is user', () => {
    const panel = panelFor('empty');
    expect(panel.style.id).toBe('oh-my-claudecode');
    expect(panel.style.name).toBe('oh-my-claudecode');
    expect(panel.style.source).toBe('user');
    // Only a bundled style goes unbadged, wherever its name appears (contract 1.10).
    expect(styleViewModel(panel).sourceBadge).toBe('사용자 등록');
    expect(styleViewModel(panel).tint).toBe('indigo');
    expect(panel.presentation.headerTitle).toBe('oh-my-claudecode · 목표');
  });

  it('carries the whole catalogue, and every id survives the id filter', () => {
    // `normalizeStylePanel` drops any id `POST /guided` would refuse, so an
    // unchanged list is the assertion that none of these would come back a 400.
    for (const recordedCase of Object.keys(recorded)) {
      expect(panelFor(recordedCase).actions.map((action) => action.id)).toEqual(ACTION_IDS);
    }
    const panel = panelFor('empty');
    expect(panel.actions).toHaveLength(16);
    // This style writes icons, not glyphs: the phone has no SF Symbol renderer,
    // so its chips fall back to the neutral mark rather than an emoji.
    expect(panel.actions.every((action) => action.glyph === undefined)).toBe(true);
    expect(panel.actions.every((action) => action.takesText)).toBe(true);
    expect(panel.actions.filter((action) => action.flags.includes('readOnly')).map((a) => a.id)).toEqual([
      'review',
      'verify',
      'external-context',
    ]);
    expect(panel.actions.filter((action) => action.requiresText).map((a) => a.id)).toEqual([
      'plan',
      'ralplan',
      'deep-interview',
      'autopilot',
      'ralph',
      'team',
      'research',
      'external-context',
      'trace',
      'debug',
    ]);
  });

  it('draws the entry chips on an empty pane and the next step after the first action', () => {
    const empty = panelFor('empty');
    expect(empty.phase).toEqual({ id: 'goal', title: '목표', index: 0, count: 5 });
    const emptyModel = styleViewModel(empty);
    // One group and no axis, so there is no map to pick from: the flat `next`
    // list is what the pane is asking for, and its head is drawn filled.
    expect(emptyModel.showMap).toBe(false);
    expect(emptyModel.actions.map((view) => view.action.id)).toEqual([
      'plan',
      'ralplan',
      'deep-interview',
      'autopilot',
      'research',
    ]);
    expect(emptyModel.actions.filter((view) => view.prominent).map((view) => view.action.id)).toEqual(['plan']);
    expect(emptyModel.rest).toHaveLength(11);
    expect(emptyModel.guidance).toBe(
      '무엇을 할까요? 목표를 아래에 적으면 계획부터 세우고 실행 · 리뷰 · 검증까지 이어갑니다.',
    );

    const after = panelFor('afterFirstAction');
    expect(after.phase).toEqual({ id: 'plan', title: '계획', index: 1, count: 5 });
    expect(after.presentation.headerTitle).toBe('oh-my-claudecode · 계획');
    const afterModel = styleViewModel(after);
    expect(afterModel.actions.map((view) => view.action.id)).toEqual(['execute', 'ralplan', 'review', 'research']);
    expect(afterModel.actions.filter((view) => view.prominent).map((view) => view.action.id)).toEqual(['execute']);
    // No capability is declared, so nothing is ever recommended.
    expect(afterModel.actions.some((view) => view.recommended)).toBe(false);
  });

  it('shows the setup block only while the Mac says the plugin is missing', () => {
    expect(styleViewModel(panelFor('empty')).setup).toBeUndefined();
    const notReady = styleViewModel(panelFor('notReady')).setup;
    expect(notReady?.missing).toEqual(['oh-my-claudecode 플러그인이 설치되어 있지 않습니다']);
    expect(notReady?.hint).toContain('Enter는 직접 누르세요.');
    // Shown as text only; the phone has no route that runs it (contract 7.3).
    expect(notReady?.installCommand).toBe(
      'claude plugin marketplace add https://github.com/Yeachan-Heo/oh-my-claudecode && claude plugin install oh-my-claudecode@omc',
    );
  });

  it('posts a guided request that carries the composer text, and drops an empty one', () => {
    const panel = panelFor('empty');
    expect(guidedRequestFor(panel, 'plan', '  결제 모듈 리팩터링  ')).toEqual({
      styleId: 'oh-my-claudecode',
      actionId: 'plan',
      text: '결제 모듈 리팩터링',
    });
    // `requiresText` is a UI hint the route does not check (contract 1.3.3), so
    // an empty composer still sends the bare action.
    expect(guidedRequestFor(panel, 'plan', '   ')).toEqual({
      styleId: 'oh-my-claudecode',
      actionId: 'plan',
      text: undefined,
    });
    for (const action of panel.actions) {
      expect(guidedRequestFor(panel, action.id, '대상').text).toBe('대상');
    }
  });
});
