import {
  blockText,
  guidedRequestFor,
  inlineText,
  legacyStylePanel,
  normalizeStylePanel,
  ouroborosPhaseLabel,
  panelOf,
  sourceBadge,
  styleOptions,
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
        style: { id: 'evil', name: '\u202egnitset\u202c', source: 'user' },
        actions: [
          {
            id: 'a\u200e1',
            title: 'a\u0007b',
            help: '\u2066거꾸로\u2069',
            takesText: false,
            requiresText: false,
          },
        ],
        next: ['a1'],
        groups: [],
        phase: undefined,
        recommended: undefined,
        attachments: [],
        presentation: { headerTitle: '\u202e위조\u202c', source: 'user' },
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
        casebook: {
          name: '2026-09-cycle',
          weight: 'full',
          files: ['DESIGN.local.md', 'NOTES.md'],
        },
      },
    })!;
    expect(panel.style.id).toBe('paperthin');
    expect(panel.groups.map((group) => group.id)).toEqual(['coil', 'edge']);
    expect(panel.recommended).toBe('re0-memo');
    // The host titles a casebook file without its `.local.md` suffix; one casebook must
    // not read as two different sets of files depending on which host answered.
    expect(panel.attachments).toEqual([
      { id: 'DESIGN.local.md', title: 'DESIGN', detail: '2026-09-cycle · full', readOnly: true },
      { id: 'NOTES.md', title: 'NOTES.md', detail: '2026-09-cycle · full', readOnly: true },
    ]);
    // Every Paperthin skill takes the composer text: it is the skill's target.
    expect(panel.actions.every((action) => action.takesText)).toBe(true);
    expect(panel.actions[1]?.flags).toEqual(['userInvoked']);

    const model = styleViewModel(panel);
    expect(model.showMap).toBe(true);
    expect(model.selectedGroupId).toBe('coil');
    expect(model.actions.map((view) => view.action.id)).toEqual(['re0-memo']);
    // Word for word the bundled manifest's `guidance.next`, so the same style says the
    // same thing on both paths; the long-press hint is the panel's, not the manifest's.
    expect(model.guidance).toBe('대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 비워 두면 스킬만 보냅니다.');
    // A map on screen means the emphasis is the group cell's, so no chip is filled.
    expect(model.actions.every((view) => !view.prominent)).toBe(true);
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

/** Contract 1.11's banned set, one scalar per entry, as code points. */
const BANNED_SCALARS = [
  0x0000, 0x0007, 0x001f, 0x007f, 0x009f, 0x00ad, 0x061c, 0x200b, 0x200c, 0x200d, 0x200e,
  0x200f, 0x2028, 0x2029, 0x202a, 0x202b, 0x202c, 0x202d, 0x202e, 0x2060, 0x2066, 0x2067,
  0x2068, 0x2069, 0xfeff,
];

describe('the §1.11 sanitisers', () => {
  it('strips every banned scalar from one-line text', () => {
    for (const code of BANNED_SCALARS) {
      const name = `Ouro${String.fromCodePoint(code)}boros`;
      expect({ code, out: inlineText(name, 40) }).toEqual({ code, out: 'Ouroboros' });
    }
  });

  it('strips every banned scalar from block text, keeping tab, newline and return', () => {
    for (const code of BANNED_SCALARS) {
      const command = `npx${String.fromCodePoint(code)} install`;
      expect({ code, out: blockText(command, 80) }).toEqual({ code, out: 'npx install' });
    }
    // The three the install box lives on: U+0009, U+000A, U+000D.
    const kept = String.fromCodePoint(9, 10, 13);
    expect(blockText(`a${kept}b`, 80)).toBe(`a${kept}b`);
    // …and they are gone from a line that has to stay one line.
    expect(inlineText(`a${kept}b`, 80)).toBe('ab');
  });
});

describe('lookups keyed by what the host sent', () => {
  // A plain object answers `__proto__` with an object and `constructor` with a function.
  // `<Text>` throws on the first and silently draws nothing for the second — which is a
  // style with no source badge, exactly what contract 1.10 exists to prevent.
  const prototypeKeys = ['__proto__', 'constructor', 'toString', 'valueOf', 'hasOwnProperty'];

  it('badges a prototype key like any other word it does not know', () => {
    for (const key of prototypeKeys) {
      expect({ key, badge: sourceBadge(key) }).toEqual({ key, badge: '출처 불명' });
    }
  });

  it('passes a prototype key through as a phase name', () => {
    for (const key of prototypeKeys) {
      expect({ key, label: ouroborosPhaseLabel(key) }).toEqual({ key, label: key });
    }
  });

  it('draws a panel whose source is a prototype key without throwing', () => {
    for (const key of prototypeKeys) {
      const panel = normalizeStylePanel(
        panelPayload({
          style: { id: 'evil', name: 'evil', source: key },
          presentation: { headerTitle: 'evil', source: key },
        }),
      )!;
      const model = styleViewModel(panel);
      expect({ key, badge: model.sourceBadge }).toEqual({ key, badge: '출처 불명' });
      expect(typeof model.headerTitle).toBe('string');
    }
  });
});

describe('styleOptions', () => {
  it('reads the host list and badges everything that is not bundled', () => {
    expect(
      styleOptions([
        { id: 'cli', label: '없음' },
        { id: 'ouroboros', label: 'Ouroboros', source: 'bundled' },
        { id: 'gstack', label: 'gstack', source: 'workspace' },
      ]),
    ).toEqual([
      { id: 'cli', label: '없음' },
      { id: 'ouroboros', label: 'Ouroboros' },
      { id: 'gstack', label: 'gstack', badge: '저장소에서 발견됨' },
    ]);
  });

  it('drops what it cannot draw and never reads a prototype key as bundled', () => {
    expect(styleOptions(undefined)).toEqual([]);
    expect(styleOptions('gstack')).toEqual([]);
    expect(styleOptions([null, 7, [], { label: '아이디 없음' }])).toEqual([]);
    // `cli` goes unbadged only when it arrives with no source at all.
    expect(styleOptions([{ id: 'cli', label: '없음', source: 'user' }])).toEqual([
      { id: 'cli', label: '없음', badge: '사용자 등록' },
    ]);
    expect(styleOptions([{ id: 'x', label: '', source: '__proto__' }])).toEqual([
      { id: 'x', label: 'x', badge: '출처 불명' },
    ]);
  });
});

describe('action ids the route would refuse', () => {
  // Contract 7.5: `^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$`. A chip for anything else could
  // only ever come back as a 400, so it is never drawn.
  it('drops them from the catalogue, the groups and the next list', () => {
    const panel = normalizeStylePanel(
      panelPayload({
        groups: [],
        actions: [
          { id: '-lead', title: '앞이 하이픈', takesText: false, requiresText: false },
          { id: '스킬', title: '한글 아이디', takesText: false, requiresText: false },
          { id: 'a/b', title: '슬래시', takesText: false, requiresText: false },
          { id: 'a'.repeat(65), title: '너무 김', takesText: false, requiresText: false },
          { id: 'ok.id:1-2_3', title: '괜찮음', takesText: false, requiresText: false },
        ],
        next: ['-lead', '스킬', 'ok.id:1-2_3'],
        recommended: '스킬',
      }),
    )!;
    expect(panel.actions.map((action) => action.id)).toEqual(['ok.id:1-2_3']);
    expect(panel.next).toEqual(['ok.id:1-2_3']);
    expect(panel.recommended).toBeUndefined();
  });
});

describe('a style with nothing to offer right now', () => {
  // The Mac sends an empty `next` while a run is in flight; the catalogue is still there
  // and "더 보기" is still the way to it.
  it('keeps the whole catalogue reachable through rest', () => {
    const panel = normalizeStylePanel(panelPayload({ groups: [], next: [] }))!;
    const model = styleViewModel(panel);
    expect(model.showMap).toBe(false);
    expect(model.actions).toEqual([]);
    expect(model.rest.map((action) => action.id)).toEqual(['spec', 'ship', 'qa']);
    expect(model.takesText).toEqual([]);
  });
});

describe('which chip is drawn filled', () => {
  it('follows the host, falling back to the head of the list', () => {
    const hostMarked = normalizeStylePanel(panelPayload({ groups: [] }))!;
    expect(
      styleViewModel(hostMarked)
        .actions.filter((view) => view.prominent)
        .map((view) => view.action.id),
    ).toEqual(['spec']);

    // Nothing marked: the step being asked for is the head of `next`.
    const unmarked = normalizeStylePanel(
      panelPayload({
        groups: [],
        actions: [
          { id: 'spec', title: '명세', takesText: false, requiresText: false },
          { id: 'ship', title: '출시', takesText: false, requiresText: false },
        ],
        next: ['spec', 'ship'],
        recommended: undefined,
      }),
    )!;
    const model = styleViewModel(unmarked);
    expect(model.actions.map((view) => view.prominent)).toEqual([true, false]);
  });

  it('draws none at all while a group map is on screen', () => {
    // The host marks one action of the group it opened on. Drawn filled, it would vanish
    // the moment the user picks any other group: the same gesture would toggle a primary
    // action in and out of existence.
    const panel = normalizeStylePanel(panelPayload())!;
    for (const groupId of [undefined, 'make', 'check']) {
      const model = styleViewModel(panel, groupId);
      expect({ groupId, prominent: model.actions.some((view) => view.prominent) }).toEqual({
        groupId,
        prominent: false,
      });
    }
  });
});

describe('guidedRequestFor against an older host', () => {
  const panel = normalizeStylePanel(panelPayload())!;

  it('marks the request legacy so the client sends the old body', () => {
    expect(guidedRequestFor(panel, 'spec', '결제 모듈', false)).toEqual({
      styleId: 'gstack',
      actionId: 'spec',
      text: '결제 모듈',
      legacy: true,
    });
    expect(guidedRequestFor(panel, 'ship', '결제 모듈', false)).toEqual({
      styleId: 'gstack',
      actionId: 'ship',
      legacy: true,
    });
    // A host that advertised "style" gets the new pair and no marker.
    expect(guidedRequestFor(panel, 'ship', '', true).legacy).toBeUndefined();
  });
});

describe('panelOf', () => {
  const generic = normalizeStylePanel(panelPayload())!;
  const ouroboros = {
    phase: 'interview',
    ready: true,
    takesText: ['interview'],
    next: [{ skill: 'interview', title: '인터뷰', help: '' }],
    all: [{ skill: 'interview', title: '인터뷰', help: '' }],
  };

  it('lets a style-aware host own the answer and reads an older one legacily', () => {
    expect(panelOf(undefined)).toBeUndefined();
    expect(panelOf({ style: 'ouroboros', runs: [], panel: generic }, true)).toBe(generic);
    // The same payload against a host without "style": the legacy adapter answers.
    const legacy = panelOf({ style: 'ouroboros', runs: [], panel: generic, ouroboros }, false);
    expect(legacy?.style.id).toBe('ouroboros');
    expect(legacy).not.toBe(generic);
    // No panel and no legacy payload at all is a plain CLI pane.
    expect(panelOf({ style: 'cli', runs: [] }, true)).toBeUndefined();
    expect(panelOf({ style: 'gstack', runs: [] }, false)).toBeUndefined();
  });

  it('says so when a style-aware host sent a panel it could not read', () => {
    // Silently drawing the legacy payload would show a working-looking pane missing its
    // stepper, its groups and its attachments, with no sign anything was dropped.
    const broken = panelOf(
      { style: 'ouroboros', runs: [], styleId: 'ouroboros', panelUnreadable: true, ouroboros },
      true,
    )!;
    expect(broken.actions).toEqual([]);
    expect(broken.setup.ready).toBe(false);
    expect(broken.setup.missing).toEqual(['호스트가 보낸 스타일 정보를 읽을 수 없습니다.']);
    expect(styleViewModel(broken).sourceBadge).toBe('출처 불명');
    // An older host never advertised "style", so the same flag leaves it alone.
    expect(
      panelOf({ style: 'ouroboros', runs: [], panelUnreadable: true, ouroboros }, false)?.style.id,
    ).toBe('ouroboros');
  });
});
