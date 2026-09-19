import {
  guidedRequestFor,
  legacyStylePanel,
  normalizeStylePanel,
  ouroborosPhaseLabel,
  sourceBadge,
  styleViewModel,
} from '@/lib/styles';

/** A panel in the shape contract 7.3 documents, with every optional field present. */
function panelPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    style: { id: 'gstack', name: 'gstack', source: 'workspace', icon: 'hammer', tint: 'teal' },
    phase: { id: 'build', title: '빌드', index: 1, count: 4 },
    groups: [
      {
        id: 'make',
        title: '만들기',
        axis: '하나 · 처음',
        question: '무엇을 세우는가?',
        selected: true,
        actions: ['spec', 'ship'],
      },
      { id: 'check', title: '점검', axis: '여럿 · 반복', selected: false, actions: ['qa'] },
    ],
    actions: [
      {
        id: 'spec',
        title: '명세',
        icon: 'doc.text',
        glyph: '📐',
        help: '의도를 명세로 굳힙니다',
        scope: '기능 하나',
        takesText: true,
        requiresText: true,
        flags: ['userInvoked'],
        prominent: true,
      },
      {
        id: 'ship',
        title: '출시',
        help: '',
        takesText: false,
        requiresText: false,
        flags: [],
        prominent: false,
      },
      {
        id: 'qa',
        title: 'QA',
        help: '훑어봅니다',
        takesText: true,
        requiresText: false,
        flags: ['readOnly'],
        prominent: false,
      },
    ],
    next: ['spec', 'ship'],
    recommended: 'qa',
    attachments: [
      { id: 'DESIGN.local.md', title: 'DESIGN', detail: '2026-09-cycle · full', readOnly: true },
    ],
    setup: {
      ready: false,
      missing: ['gstack 스킬이 없습니다.'],
      hint: '설치 후 다시 시도하세요.',
      installCommand: 'npx gstack install',
    },
    guidance: '빌드 단계가 끝났습니다.',
    presentation: {
      headerTitle: 'gstack · 빌드',
      source: 'workspace',
      icon: 'hammer',
      tint: 'teal',
    },
    ...overrides,
  };
}

describe('normalizeStylePanel', () => {
  it('reads the documented payload', () => {
    const panel = normalizeStylePanel(panelPayload());
    expect(panel?.style).toEqual({ id: 'gstack', name: 'gstack', source: 'workspace', tint: 'teal' });
    expect(panel?.phase).toEqual({ id: 'build', title: '빌드', index: 1, count: 4 });
    expect(panel?.groups.map((group) => group.id)).toEqual(['make', 'check']);
    expect(panel?.groups[0]?.question).toBe('무엇을 세우는가?');
    expect(panel?.actions).toHaveLength(3);
    expect(panel?.actions[0]).toEqual({
      id: 'spec',
      title: '명세',
      glyph: '📐',
      help: '의도를 명세로 굳힙니다',
      scope: '기능 하나',
      takesText: true,
      requiresText: true,
      flags: ['userInvoked'],
      prominent: true,
    });
    expect(panel?.next).toEqual(['spec', 'ship']);
    expect(panel?.recommended).toBe('qa');
    expect(panel?.attachments[0]?.detail).toBe('2026-09-cycle · full');
    expect(panel?.setup).toEqual({
      ready: false,
      missing: ['gstack 스킬이 없습니다.'],
      hint: '설치 후 다시 시도하세요.',
      installCommand: 'npx gstack install',
    });
    expect(panel?.guidance).toBe('빌드 단계가 끝났습니다.');
    expect(panel?.presentation.headerTitle).toBe('gstack · 빌드');
  });

  it('refuses only a panel it could never post an action for', () => {
    expect(normalizeStylePanel(undefined)).toBeUndefined();
    expect(normalizeStylePanel([])).toBeUndefined();
    expect(normalizeStylePanel({ style: 'gstack' })).toBeUndefined();
    expect(normalizeStylePanel({ style: { id: '', name: 'x' } })).toBeUndefined();
    // Everything else is optional: an empty style still draws its name.
    const bare = normalizeStylePanel({ style: { id: 'x', name: '' } });
    expect(bare?.style.name).toBe('x');
    expect(bare?.actions).toEqual([]);
    expect(bare?.next).toEqual([]);
    expect(bare?.setup).toEqual({ ready: true, missing: [] });
    expect(bare?.presentation).toEqual({ headerTitle: 'x', source: '' });
  });

  it('drops what it cannot draw instead of throwing', () => {
    const panel = normalizeStylePanel(
      panelPayload({
        groups: [
          null,
          { title: '아이디 없음', actions: ['spec'] },
          { id: 'ghost', title: '없는 행동만', actions: ['nope'] },
          { id: 'make', title: '만들기', selected: true, actions: ['spec', 'nope', 'spec'] },
        ],
        actions: [
          7,
          { title: '아이디 없음' },
          { id: 'spec', title: '명세', takesText: true },
          { id: 'spec', title: '두 번째 명세' },
        ],
        next: ['spec', 'nope', 'spec', 42],
        recommended: 'nope',
        attachments: [{ title: '아이디 없음' }, 'plain'],
        setup: { missing: ['', 3, '진짜'] },
      }),
    );
    expect(panel?.groups.map((group) => group.id)).toEqual(['make']);
    expect(panel?.groups[0]?.actions).toEqual(['spec']);
    expect(panel?.actions.map((action) => action.id)).toEqual(['spec']);
    expect(panel?.next).toEqual(['spec']);
    expect(panel?.recommended).toBeUndefined();
    expect(panel?.attachments).toEqual([]);
    expect(panel?.setup.missing).toEqual(['진짜']);
  });

  it('strips control and bidi characters from what it will draw', () => {
    const panel = normalizeStylePanel(
      panelPayload({
        style: { id: 'evil', name: '‮gnitset‬', source: 'user' },
        actions: [
          {
            id: 'a‎1',
            title: 'a\u0007b',
            help: '⁦거꾸로⁩',
            takesText: false,
            requiresText: false,
          },
        ],
        next: ['a1'],
        groups: [],
        phase: undefined,
        recommended: undefined,
        attachments: [],
        presentation: { headerTitle: '‮위조‬', source: 'user' },
      }),
    );
    expect(panel?.style.name).toBe('gnitset');
    expect(panel?.actions[0]?.id).toBe('a1');
    expect(panel?.actions[0]?.title).toBe('ab');
    expect(panel?.actions[0]?.help).toBe('거꾸로');
    expect(panel?.next).toEqual(['a1']);
    expect(panel?.presentation.headerTitle).toBe('위조');
  });

  it('keeps a palette or a flag it does not know from reaching the screen', () => {
    const panel = normalizeStylePanel(
      panelPayload({
        style: { id: 'gstack', name: 'gstack', source: 'workspace', tint: 'chartreuse' },
        actions: [
          {
            id: 'spec',
            title: '명세',
            takesText: false,
            requiresText: false,
            flags: ['readOnly', 'godMode', 'readOnly'],
          },
        ],
        next: ['spec'],
        groups: [],
        recommended: undefined,
      }),
    );
    // An unknown palette name travels; the theme draws it in the accent (contract 1.10).
    expect(panel?.style.tint).toBe('chartreuse');
    expect(panel?.actions[0]?.flags).toEqual(['readOnly']);
    expect(() => styleViewModel(panel!)).not.toThrow();
  });

  it('refuses a phase index that does not fit its count', () => {
    const phased = (phase: unknown) => normalizeStylePanel(panelPayload({ phase }))?.phase;
    expect(phased({ id: 'p', title: '단계', index: 9, count: 4 })?.index).toBe(0);
    expect(phased({ id: 'p', title: '단계', index: -1, count: 4 })?.index).toBe(0);
    expect(phased({ id: 'p', title: '단계', index: 1.5, count: 4 })?.index).toBe(0);
    expect(phased({ id: 'p', title: '단계' })).toEqual({ id: 'p', title: '단계', index: 0, count: 0 });
    expect(phased({ title: '아이디 없음' })).toBeUndefined();
  });

  it('cuts the lists the host promised to keep short', () => {
    const actions = Array.from({ length: 140 }, (_, index) => ({
      id: `a${index}`,
      title: `행동 ${index}`,
      takesText: false,
      requiresText: false,
    }));
    const panel = normalizeStylePanel(
      panelPayload({
        actions,
        next: actions.map((action) => action.id),
        groups: [],
        recommended: undefined,
        attachments: Array.from({ length: 30 }, (_, index) => ({
          id: `f${index}`,
          title: `f${index}`,
        })),
      }),
    );
    expect(panel?.actions).toHaveLength(100);
    expect(panel?.next).toHaveLength(100);
    expect(panel?.attachments).toHaveLength(24);
  });
});

describe('sourceBadge', () => {
  it('leaves only a bundled style unbadged', () => {
    expect(sourceBadge('bundled')).toBeUndefined();
    expect(sourceBadge('user')).toBe('사용자 등록');
    expect(sourceBadge('workspace')).toBe('저장소에서 발견됨');
    // A name can be forged, so a source we cannot read never passes as bundled.
    expect(sourceBadge('Bundled')).toBeDefined();
    expect(sourceBadge('')).toBeDefined();
    expect(sourceBadge(undefined)).toBeDefined();
  });
});

describe('styleViewModel', () => {
  it('draws the next actions in order, the first of them prominent', () => {
    const panel = normalizeStylePanel(panelPayload({ groups: [] }))!;
    const model = styleViewModel(panel);
    expect(model.showMap).toBe(false);
    expect(model.actions.map((view) => view.action.id)).toEqual(['spec', 'ship']);
    expect(model.actions[0]?.prominent).toBe(true);
    expect(model.actions[1]?.prominent).toBe(false);
    expect(model.rest.map((action) => action.id)).toEqual(['qa']);
    expect(model.takesText.map((action) => action.id)).toEqual(['spec']);
  });

  it('draws the group the host selected, then the one the user picks', () => {
    const panel = normalizeStylePanel(panelPayload())!;
    const first = styleViewModel(panel);
    expect(first.showMap).toBe(true);
    expect(first.selectedGroupId).toBe('make');
    expect(first.question).toBe('무엇을 세우는가?');
    expect(first.actions.map((view) => view.action.id)).toEqual(['spec', 'ship']);

    const second = styleViewModel(panel, 'check');
    expect(second.selectedGroupId).toBe('check');
    expect(second.question).toBeUndefined();
    expect(second.actions.map((view) => view.action.id)).toEqual(['qa']);
    expect(second.actions[0]?.recommended).toBe(true);
    expect(second.rest.map((action) => action.id)).toEqual(['spec', 'ship']);
  });

  it('treats groups without an axis as sections, not a map, and still reaches them', () => {
    // Contract 1.4: a map needs two groups and an axis between them. Without one the
    // pane is asking for `next`, and the rest of the catalogue waits in "더 보기".
    const panel = normalizeStylePanel(
      panelPayload({
        groups: [
          { id: 'make', title: '만들기', selected: true, actions: ['spec', 'ship'] },
          { id: 'check', title: '점검', selected: false, actions: ['qa'] },
        ],
      }),
    )!;
    const model = styleViewModel(panel);
    expect(model.showMap).toBe(false);
    expect(model.selectedGroupId).toBeUndefined();
    expect(model.actions.map((view) => view.action.id)).toEqual(['spec', 'ship']);
    expect(model.rest.map((action) => action.id)).toEqual(['qa']);
  });

  it('shows the source badge and the setup block, and hides setup once ready', () => {
    const panel = normalizeStylePanel(panelPayload())!;
    const model = styleViewModel(panel);
    expect(model.headerTitle).toBe('gstack · 빌드');
    expect(model.sourceBadge).toBe('저장소에서 발견됨');
    expect(model.tint).toBe('teal');
    expect(model.setup).toEqual({
      missing: ['gstack 스킬이 없습니다.'],
      hint: '설치 후 다시 시도하세요.',
      installCommand: 'npx gstack install',
    });

    const ready = normalizeStylePanel(
      panelPayload({
        style: { id: 'ouroboros', name: 'Ouroboros', source: 'bundled' },
        presentation: { headerTitle: 'Ouroboros', source: 'bundled' },
        setup: { ready: true, missing: [] },
      }),
    )!;
    const readyModel = styleViewModel(ready);
    expect(readyModel.sourceBadge).toBeUndefined();
    expect(readyModel.setup).toBeUndefined();
  });
});

describe('guidedRequestFor', () => {
  const panel = normalizeStylePanel(panelPayload())!;

  it('carries the composer text only for an action that takes it', () => {
    expect(guidedRequestFor(panel, 'spec', '  결제 모듈  ')).toEqual({
      styleId: 'gstack',
      actionId: 'spec',
      text: '결제 모듈',
    });
    expect(guidedRequestFor(panel, 'ship', '결제 모듈')).toEqual({
      styleId: 'gstack',
      actionId: 'ship',
    });
  });

  it('sends the bare action for an empty composer, hint or no hint', () => {
    // `requiresText` is a UI hint the route does not check (contract 1.3.3).
    expect(guidedRequestFor(panel, 'spec', '   ')).toEqual({
      styleId: 'gstack',
      actionId: 'spec',
    });
    expect(guidedRequestFor(panel, 'nope', '글')).toEqual({
      styleId: 'gstack',
      actionId: 'nope',
    });
  });
});

describe('ouroborosPhaseLabel', () => {
  it('uses the Mac wording and passes an unknown phase through', () => {
    expect(ouroborosPhaseLabel('interview')).toBe('인터뷰');
    expect(ouroborosPhaseLabel('evolve')).toBe('진화');
    expect(ouroborosPhaseLabel('transcend')).toBe('transcend');
    expect(ouroborosPhaseLabel('')).toBe('단계');
  });
});

describe('legacyStylePanel', () => {
  it('folds the Ouroboros payload into the same panel', () => {
    const panel = legacyStylePanel({
      style: 'ouroboros',
      runs: [],
      ouroboros: {
        phase: 'interview',
        ready: false,
        takesText: ['interview'],
        next: [{ skill: 'seed', title: '시드 생성', help: '명세로 굳힙니다' }],
        all: [
          { skill: 'interview', title: '인터뷰', help: '' },
          { skill: 'seed', title: '시드 생성', help: '명세로 굳힙니다' },
        ],
      },
    })!;
    expect(panel.style).toEqual({ id: 'ouroboros', name: 'Ouroboros', source: 'bundled' });
    expect(panel.presentation.headerTitle).toBe('Ouroboros · 인터뷰');
    // The old payload has no phase ordering, so there is no stepper to draw.
    expect(panel.phase).toEqual({ id: 'interview', title: '인터뷰', index: 0, count: 0 });
    expect(panel.groups).toEqual([]);
    expect(panel.next).toEqual(['seed']);
    expect(panel.actions.map((action) => action.id)).toEqual(['interview', 'seed']);
    expect(panel.actions[0]?.takesText).toBe(true);
    expect(panel.actions[1]?.takesText).toBe(false);
    expect(panel.setup.ready).toBe(false);
    expect(panel.setup.missing).toHaveLength(1);
    expect(panel.setup.installCommand).toBeUndefined();

    const model = styleViewModel(panel);
    expect(model.sourceBadge).toBeUndefined();
    expect(model.actions.map((view) => view.action.id)).toEqual(['seed']);
    expect(model.rest.map((action) => action.id)).toEqual(['interview']);
  });

  it('folds the Paperthin payload into a group map with its casebook', () => {
    const panel = legacyStylePanel({
      style: 'paperthin',
      runs: [],
      paperthin: {
        installed: true,
        recommended: 're0-memo',
        domains: [
          {
            id: 'coil',
            title: 'coil',
            axis: '하나 · 반복',
            question: '각 패스가 다음 패스를 가르쳤는가?',
            skills: [
              {
                name: 're0-memo',
                emoji: '🧭',
                summary: '교훈을 뽑아냅니다',
                scope: '완료된 사이클 하나',
                userInvoked: false,
                readOnly: true,
              },
            ],
          },
          {
            id: 'edge',
            title: 'edge',
            axis: '여럿 · 처음',
            question: '',
            skills: [
              {
                name: 'nba',
                emoji: '🎯',
                summary: '다음 한 수',
                scope: '',
                userInvoked: true,
                readOnly: false,
              },
            ],
          },
        ],
        casebook: { name: '2026-09-cycle', weight: 'full', files: ['DESIGN.md', 'RETRO.md'] },
      },
    })!;
    expect(panel.style.id).toBe('paperthin');
    expect(panel.groups.map((group) => group.id)).toEqual(['coil', 'edge']);
    expect(panel.recommended).toBe('re0-memo');
    expect(panel.attachments).toEqual([
      { id: 'DESIGN.md', title: 'DESIGN.md', detail: '2026-09-cycle · full', readOnly: true },
      { id: 'RETRO.md', title: 'RETRO.md', detail: '2026-09-cycle · full', readOnly: true },
    ]);
    // Every Paperthin skill takes the composer text: it is the skill's target.
    expect(panel.actions.every((action) => action.takesText)).toBe(true);
    expect(panel.actions[1]?.flags).toEqual(['userInvoked']);

    const model = styleViewModel(panel);
    expect(model.showMap).toBe(true);
    expect(model.selectedGroupId).toBe('coil');
    expect(model.actions.map((view) => view.action.id)).toEqual(['re0-memo']);
    expect(model.guidance).toContain('길게 누르면');
    expect(guidedRequestFor(panel, 're0-memo', 'docs/spec.md')).toEqual({
      styleId: 'paperthin',
      actionId: 're0-memo',
      text: 'docs/spec.md',
    });
  });

  it('answers for the two built-in ids and nothing else', () => {
    expect(legacyStylePanel({ style: 'gstack', runs: [] })).toBeUndefined();
    expect(legacyStylePanel({ style: 'cli', runs: [] })).toBeUndefined();
    // The id without its payload has nothing to fold.
    expect(legacyStylePanel({ style: 'ouroboros', runs: [] })).toBeUndefined();
  });
});
