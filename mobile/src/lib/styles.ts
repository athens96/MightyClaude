import type {
  GuidedRequest,
  MobileMighty,
  MobileOuroboros,
  MobilePaperthin,
  OuroborosAction,
  PaperthinDomain,
  PaperthinSkill,
  StyleAction,
  StyleAttachment,
  StyleGroup,
  StylePanel,
  StylePhase,
} from '@/api/types';

/**
 * The Mighty style engine, seen from the phone. The host projects whichever manifest a
 * pane is running into one `panel` payload; everything here reads that payload
 * defensively (a field the host adds later, or one it leaves out, must never crash a
 * screen) and turns it into what the guided panel draws.
 *
 * The phone holds no closed set of style ids: a panel arrives, it is drawn. The two
 * built-in styles also keep sending their old `ouroboros`/`paperthin` payloads, and a
 * host that never learned the "style" capability sends only those — `legacyStylePanel`
 * folds them into the same shape so one component covers both.
 */

/** Counts the host promises to stay under; a longer list is cut rather than refused. */
export const MAX_STYLE_ACTIONS = 100;
export const MAX_STYLE_GROUPS = 16;
export const MAX_STYLE_ATTACHMENTS = 24;
export const MAX_SETUP_MISSING = 16;

/** The two marks an action chip draws; a flag outside this set is dropped, not guessed. */
export const ACTION_FLAGS = ['userInvoked', 'readOnly'] as const;

/**
 * C0/C1 controls and the bidi overrides, which can reorder what a row appears to say.
 * The Mac normalises manifest strings before they leave; this is the second layer.
 */
const UNSAFE_INLINE = /[\u0000-\u001f\u007f-\u009f‎‏‪-‮⁦-⁩]/g;
const UNSAFE_BLOCK =
  /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f‎‏‪-‮⁦-⁩]/g;

/** One line of safe text, trimmed and cut; anything that is not a string becomes ''. */
export function inlineText(value: unknown, limit: number): string {
  if (typeof value !== 'string') return '';
  return value.replace(UNSAFE_INLINE, '').trim().slice(0, limit);
}

/** Safe text that may span lines; used for block output and install commands. */
export function blockText(value: unknown, limit: number): string {
  if (typeof value !== 'string') return '';
  return value.replace(UNSAFE_BLOCK, '').slice(0, limit);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function arrayOf(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function stringList(value: unknown): string[] {
  return arrayOf(value).filter(
    (entry): entry is string => typeof entry === 'string' && entry.length > 0,
  );
}

// ------------------------------------------------------------------ presentation

const SOURCE_BADGES: Record<string, string> = {
  user: '사용자 등록',
  workspace: '저장소에서 발견됨',
};

/**
 * The badge drawn next to a style's name. Only the exact word `bundled` goes without
 * one: a name can be forged, a source cannot, so anything else — a word this app does
 * not know, or no word at all — is called out rather than trusted (contract 1.10).
 */
export function sourceBadge(source: string | undefined): string | undefined {
  if (source === 'bundled') return undefined;
  return (source === undefined ? undefined : SOURCE_BADGES[source]) ?? '출처 불명';
}

/** The Mac's names for the Ouroboros phases; an unknown id is shown as it arrived. */
const PHASE_LABELS: Record<string, string> = {
  goal: '목표',
  interview: '인터뷰',
  seed: '시드',
  run: '실행',
  evaluate: '평가',
  evolve: '진화',
};

export function ouroborosPhaseLabel(phase: string): string {
  return PHASE_LABELS[phase] ?? (phase.length > 0 ? phase : '단계');
}

// ------------------------------------------------------------------ parsing

function parseAction(raw: unknown): StyleAction | undefined {
  if (!isRecord(raw)) return undefined;
  const id = inlineText(raw.id, 64);
  if (id.length === 0) return undefined;
  const title = inlineText(raw.title, 40);
  const action: StyleAction = {
    id,
    title: title.length > 0 ? title : id,
    help: inlineText(raw.help, 400),
    takesText: raw.takesText === true,
    requiresText: raw.requiresText === true,
    flags: ACTION_FLAGS.filter((flag) => arrayOf(raw.flags).includes(flag)),
    prominent: raw.prominent === true,
  };
  // One grapheme by contract, but a family emoji is several code units, so the cut
  // matches what the Paperthin payload already carries.
  const glyph = inlineText(raw.glyph, 8);
  if (glyph.length > 0) action.glyph = glyph;
  const scope = inlineText(raw.scope, 120);
  if (scope.length > 0) action.scope = scope;
  return action;
}

function parseGroup(raw: unknown, known: ReadonlySet<string>): StyleGroup | undefined {
  if (!isRecord(raw)) return undefined;
  const id = inlineText(raw.id, 40);
  if (id.length === 0) return undefined;
  const actions = actionIdList(raw.actions, known);
  // A group whose every action is missing from the catalogue has nothing to show.
  if (actions.length === 0) return undefined;
  const title = inlineText(raw.title, 24);
  const group: StyleGroup = {
    id,
    title: title.length > 0 ? title : id,
    selected: raw.selected === true,
    actions,
  };
  const axis = inlineText(raw.axis, 60);
  if (axis.length > 0) group.axis = axis;
  const question = inlineText(raw.question, 200);
  if (question.length > 0) group.question = question;
  return group;
}

/** Action ids in the order given, without repeats and without ones we cannot draw. */
function actionIdList(raw: unknown, known: ReadonlySet<string>): string[] {
  const seen = new Set<string>();
  const ids: string[] = [];
  for (const entry of arrayOf(raw)) {
    const id = inlineText(entry, 64);
    if (id.length === 0 || seen.has(id) || !known.has(id)) continue;
    seen.add(id);
    ids.push(id);
  }
  return ids.slice(0, MAX_STYLE_ACTIONS);
}

function parseAttachment(raw: unknown): StyleAttachment | undefined {
  if (!isRecord(raw)) return undefined;
  const id = inlineText(raw.id, 120);
  if (id.length === 0) return undefined;
  const title = inlineText(raw.title, 80);
  const attachment: StyleAttachment = {
    id,
    title: title.length > 0 ? title : id,
    readOnly: raw.readOnly !== false,
  };
  const detail = inlineText(raw.detail, 80);
  if (detail.length > 0) attachment.detail = detail;
  return attachment;
}

function parsePhase(raw: unknown): StylePhase | undefined {
  if (!isRecord(raw)) return undefined;
  const id = inlineText(raw.id, 40);
  if (id.length === 0) return undefined;
  const title = inlineText(raw.title, 24);
  const count = counter(raw.count);
  const index = counter(raw.index);
  return {
    id,
    title: title.length > 0 ? title : id,
    index: index < count ? index : 0,
    count,
  };
}

/** A non-negative whole number the host sent, or 0 for anything else. */
function counter(value: unknown): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) return 0;
  return value;
}

/**
 * Reads `MobileMighty.panel`. Returns undefined when the host sent nothing we can draw —
 * above all a style without an id, which no `/guided` request could name.
 */
export function normalizeStylePanel(raw: unknown): StylePanel | undefined {
  if (!isRecord(raw)) return undefined;
  if (!isRecord(raw.style)) return undefined;
  const styleId = inlineText(raw.style.id, 40);
  if (styleId.length === 0) return undefined;
  const name = inlineText(raw.style.name, 40);
  const styleName = name.length > 0 ? name : styleId;
  const source = inlineText(raw.style.source, 40);

  const actions = arrayOf(raw.actions)
    .map(parseAction)
    .filter((action): action is StyleAction => action !== undefined)
    .slice(0, MAX_STYLE_ACTIONS);
  // Two entries claiming the same id would draw two chips that do the same thing.
  const known = new Set(actions.map((action) => action.id));

  const setupRaw = isRecord(raw.setup) ? raw.setup : {};
  const setup: StylePanel['setup'] = {
    ready: setupRaw.ready !== false,
    missing: arrayOf(setupRaw.missing)
      .map((entry) => inlineText(entry, 200))
      .filter((entry) => entry.length > 0)
      .slice(0, MAX_SETUP_MISSING),
  };
  const hint = inlineText(setupRaw.hint, 200);
  if (hint.length > 0) setup.hint = hint;
  const installCommand = blockText(setupRaw.installCommand, 400);
  if (installCommand.length > 0) setup.installCommand = installCommand;

  const presentationRaw = isRecord(raw.presentation) ? raw.presentation : {};
  const headerTitle = inlineText(presentationRaw.headerTitle, 80);
  const panel: StylePanel = {
    style: { id: styleId, name: styleName, source },
    groups: dedupeById(
      arrayOf(raw.groups)
        .map((group) => parseGroup(group, known))
        .filter((group): group is StyleGroup => group !== undefined),
    ).slice(0, MAX_STYLE_GROUPS),
    actions: dedupeById(actions),
    next: actionIdList(raw.next, known),
    attachments: arrayOf(raw.attachments)
      .map(parseAttachment)
      .filter((entry): entry is StyleAttachment => entry !== undefined)
      .slice(0, MAX_STYLE_ATTACHMENTS),
    setup,
    presentation: {
      headerTitle: headerTitle.length > 0 ? headerTitle : styleName,
      source: inlineText(presentationRaw.source, 40) || source,
    },
  };

  const tint = inlineText(raw.style.tint, 40);
  if (tint.length > 0) panel.style.tint = tint;
  const presentationTint = inlineText(presentationRaw.tint, 40);
  if (presentationTint.length > 0) panel.presentation.tint = presentationTint;
  const phase = parsePhase(raw.phase);
  if (phase) panel.phase = phase;
  const recommended = inlineText(raw.recommended, 64);
  if (recommended.length > 0 && known.has(recommended)) panel.recommended = recommended;
  const guidance = inlineText(raw.guidance, 160);
  if (guidance.length > 0) panel.guidance = guidance;
  return panel;
}

function dedupeById<T extends { id: string }>(entries: T[]): T[] {
  const seen = new Set<string>();
  return entries.filter((entry) => {
    if (seen.has(entry.id)) return false;
    seen.add(entry.id);
    return true;
  });
}

// ------------------------------------------------------------------ legacy payloads

const OUROBOROS_MISSING = 'Mac에서 Ouroboros 준비가 끝나지 않았습니다 (플러그인·uvx).';
const OUROBOROS_HINT = 'Mac의 실행 창에서 설치한 뒤 다시 시도하세요.';
const PAPERTHIN_MISSING = 'Paperthin 스킬이 설치되어 있지 않습니다.';
const PAPERTHIN_HINT = '스킬 설치는 Mac에서 합니다.';
const PAPERTHIN_GUIDANCE = '대상(파일 경로나 지시)을 아래에 적고 스킬을 누르세요. 길게 누르면 설명이 나옵니다.';

function parseOuroborosAction(raw: unknown): OuroborosAction | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.skill !== 'string' || raw.skill.length === 0) return undefined;
  const title = inlineText(raw.title, 80);
  return {
    skill: raw.skill,
    title: title.length > 0 ? title : raw.skill,
    help: inlineText(raw.help, 400),
  };
}

/** Reads `MobileMighty.ouroboros`, the payload the built-in style keeps sending. */
export function normalizeLegacyOuroboros(raw: unknown): MobileOuroboros | undefined {
  if (!isRecord(raw)) return undefined;
  const actions = (value: unknown) =>
    arrayOf(value)
      .map(parseOuroborosAction)
      .filter((action): action is OuroborosAction => action !== undefined);
  return {
    phase: inlineText(raw.phase, 40),
    ready: raw.ready !== false,
    takesText: stringList(raw.takesText),
    next: actions(raw.next),
    all: actions(raw.all),
  };
}

function parsePaperthinSkill(raw: unknown): PaperthinSkill | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.name !== 'string' || raw.name.length === 0) return undefined;
  return {
    name: raw.name,
    emoji: inlineText(raw.emoji, 8),
    summary: inlineText(raw.summary, 400),
    scope: inlineText(raw.scope, 120),
    userInvoked: raw.userInvoked === true,
    readOnly: raw.readOnly === true,
  };
}

function parsePaperthinDomain(raw: unknown): PaperthinDomain | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const title = inlineText(raw.title, 40);
  return {
    id: raw.id,
    title: title.length > 0 ? title : raw.id,
    axis: inlineText(raw.axis, 60),
    question: inlineText(raw.question, 200),
    skills: arrayOf(raw.skills)
      .map(parsePaperthinSkill)
      .filter((skill): skill is PaperthinSkill => skill !== undefined),
  };
}

/** Reads `MobileMighty.paperthin`, the payload the built-in style keeps sending. */
export function normalizeLegacyPaperthin(raw: unknown): MobilePaperthin | undefined {
  if (!isRecord(raw)) return undefined;
  const paperthin: MobilePaperthin = {
    installed: raw.installed !== false,
    domains: arrayOf(raw.domains)
      .map(parsePaperthinDomain)
      .filter((domain): domain is PaperthinDomain => domain !== undefined),
  };
  if (typeof raw.recommended === 'string' && raw.recommended.length > 0) {
    paperthin.recommended = raw.recommended;
  }
  if (isRecord(raw.casebook) && typeof raw.casebook.name === 'string') {
    paperthin.casebook = {
      name: inlineText(raw.casebook.name, 120),
      weight: inlineText(raw.casebook.weight, 40),
      files: stringList(raw.casebook.files).map((file) => inlineText(file, 120)),
    };
  }
  return paperthin;
}

function ouroborosPanel(ouroboros: MobileOuroboros): StylePanel {
  const catalogue = [...ouroboros.all, ...ouroboros.next];
  const actions = dedupeById(
    catalogue.map((entry) => ({
      id: entry.skill,
      title: entry.title,
      help: entry.help,
      takesText: ouroboros.takesText.includes(entry.skill),
      requiresText: false,
      flags: [],
      prominent: false,
    })),
  );
  const known = new Set(actions.map((action) => action.id));
  const phaseTitle = ouroborosPhaseLabel(ouroboros.phase);
  const panel: StylePanel = {
    style: { id: 'ouroboros', name: 'Ouroboros', source: 'bundled' },
    groups: [],
    actions,
    next: actionIdList(
      ouroboros.next.map((entry) => entry.skill),
      known,
    ),
    attachments: [],
    setup: ouroboros.ready
      ? { ready: true, missing: [] }
      : { ready: false, missing: [OUROBOROS_MISSING], hint: OUROBOROS_HINT },
    presentation: {
      headerTitle: ouroboros.phase.length > 0 ? `Ouroboros · ${phaseTitle}` : 'Ouroboros',
      source: 'bundled',
    },
  };
  // The old payload carries a phase name but no ordering, so there is no stepper to
  // draw: the name rides in the header title, exactly where it is today.
  if (ouroboros.phase.length > 0) {
    panel.phase = { id: ouroboros.phase, title: phaseTitle, index: 0, count: 0 };
  }
  return panel;
}

function paperthinAction(skill: PaperthinSkill): StyleAction {
  const flags: string[] = [];
  if (skill.userInvoked) flags.push('userInvoked');
  if (skill.readOnly) flags.push('readOnly');
  const action: StyleAction = {
    id: skill.name,
    title: skill.name,
    help: skill.summary,
    // Every Paperthin skill takes the composer text: it is the skill's target.
    takesText: true,
    requiresText: false,
    flags,
    prominent: false,
  };
  if (skill.emoji.length > 0) action.glyph = skill.emoji;
  if (skill.scope.length > 0) action.scope = skill.scope;
  return action;
}

function paperthinGroup(domain: PaperthinDomain, index: number): StyleGroup {
  const group: StyleGroup = {
    id: domain.id,
    title: domain.title,
    selected: index === 0,
    actions: domain.skills.map((skill) => skill.name),
  };
  if (domain.axis.length > 0) group.axis = domain.axis;
  if (domain.question.length > 0) group.question = domain.question;
  return group;
}

function paperthinPanel(paperthin: MobilePaperthin): StylePanel {
  const actions = dedupeById(
    paperthin.domains.flatMap((domain) => domain.skills.map(paperthinAction)),
  );
  const known = new Set(actions.map((action) => action.id));
  const groups = dedupeById(paperthin.domains.map(paperthinGroup))
    .map((group) => ({ ...group, actions: actionIdList(group.actions, known) }))
    .filter((group) => group.actions.length > 0);
  const casebook = paperthin.casebook;
  const detail = casebook
    ? [casebook.name, casebook.weight].filter((part) => part.length > 0).join(' · ')
    : '';
  const panel: StylePanel = {
    style: { id: 'paperthin', name: 'Paperthin', source: 'bundled' },
    groups,
    actions,
    next: groups[0]?.actions ?? [],
    attachments: (casebook?.files ?? []).map((file) => {
      const attachment: StyleAttachment = { id: file, title: file, readOnly: true };
      if (detail.length > 0) attachment.detail = detail;
      return attachment;
    }),
    setup: paperthin.installed
      ? { ready: true, missing: [] }
      : { ready: false, missing: [PAPERTHIN_MISSING], hint: PAPERTHIN_HINT },
    guidance: PAPERTHIN_GUIDANCE,
    presentation: { headerTitle: 'Paperthin', source: 'bundled' },
  };
  if (paperthin.recommended && known.has(paperthin.recommended)) {
    panel.recommended = paperthin.recommended;
  }
  return panel;
}

/**
 * The built-in styles as a panel, for a host that has not learned the "style"
 * capability. It answers for those two ids and nothing else (contract 7.4).
 */
export function legacyStylePanel(mighty: MobileMighty): StylePanel | undefined {
  if (mighty.style === 'ouroboros' && mighty.ouroboros) return ouroborosPanel(mighty.ouroboros);
  if (mighty.style === 'paperthin' && mighty.paperthin) return paperthinPanel(mighty.paperthin);
  return undefined;
}

/**
 * The panel to draw for a pane, or undefined for a plain CLI one. A host that advertised
 * "style" owns the answer; an older host is read through the legacy adapter.
 */
export function panelOf(
  mighty: MobileMighty | undefined,
  styleAware = true,
): StylePanel | undefined {
  if (!mighty) return undefined;
  if (styleAware && mighty.panel) return mighty.panel;
  return legacyStylePanel(mighty);
}

// ------------------------------------------------------------------ view model

export interface StyleActionView {
  action: StyleAction;
  /** Drawn filled: the step the host puts first. */
  prominent: boolean;
  recommended: boolean;
}

export interface StyleSetupView {
  missing: string[];
  hint?: string;
  /** Shown as text only. The phone never runs it and never pre-fills it. */
  installCommand?: string;
}

export interface StyleViewModel {
  headerTitle: string;
  /** Absent only for a bundled style. */
  sourceBadge?: string;
  tint?: string;
  /** Drawn as a stepper only when the host sent an ordering. */
  phase?: StylePhase;
  /** True when the groups are a map to pick from rather than plain sections. */
  showMap: boolean;
  groups: StyleGroup[];
  selectedGroupId?: string;
  question?: string;
  actions: StyleActionView[];
  /** Catalogue entries this screen does not show; the "더 보기" sheet lists them. */
  rest: StyleAction[];
  attachments: StyleAttachment[];
  /** Absent once the Mac reports the style ready. */
  setup?: StyleSetupView;
  guidance?: string;
  /** Shown actions that carry the composer text, for the hint line. */
  takesText: StyleAction[];
}

/**
 * What the guided panel draws, for a panel and the group the user is looking at. Group
 * choice is the phone's own: there is no route that moves the Mac's selection, and the
 * whole catalogue travels precisely so every group can be drawn from here.
 */
export function styleViewModel(panel: StylePanel, selectedGroupId?: string): StyleViewModel {
  const byId = new Map(panel.actions.map((action) => [action.id, action]));
  // Two or more groups with an axis between them are a map to pick from; anything less
  // is plain sectioning, and the flat `next` list is what the pane is asking for
  // (contract 1.4). Either way the whole catalogue stays reachable through `rest`.
  const showMap = panel.groups.length >= 2 && panel.groups.some((group) => group.axis);
  const selected = showMap
    ? (panel.groups.find((group) => group.id === selectedGroupId) ??
      panel.groups.find((group) => group.selected) ??
      panel.groups[0])
    : undefined;
  const shownIds = selected ? selected.actions : panel.next;
  const shown = shownIds
    .map((id) => byId.get(id))
    .filter((action): action is StyleAction => action !== undefined);
  const drawn = new Set(shown.map((action) => action.id));

  const model: StyleViewModel = {
    headerTitle: panel.presentation.headerTitle,
    showMap,
    groups: panel.groups,
    actions: shown.map((action, index) => ({
      action,
      prominent: action.prominent || (!showMap && index === 0),
      recommended: action.id === panel.recommended,
    })),
    rest: panel.actions.filter((action) => !drawn.has(action.id)),
    attachments: panel.attachments,
    takesText: shown.filter((action) => action.takesText),
  };
  const badge = sourceBadge(panel.presentation.source);
  if (badge) model.sourceBadge = badge;
  const tint = panel.presentation.tint ?? panel.style.tint;
  if (tint) model.tint = tint;
  if (panel.phase) model.phase = panel.phase;
  if (selected) {
    model.selectedGroupId = selected.id;
    if (selected.question) model.question = selected.question;
  }
  if (!panel.setup.ready) {
    const setup: StyleSetupView = { missing: panel.setup.missing };
    if (panel.setup.hint) setup.hint = panel.setup.hint;
    if (panel.setup.installCommand) setup.installCommand = panel.setup.installCommand;
    model.setup = setup;
  }
  if (panel.guidance) model.guidance = panel.guidance;
  return model;
}

/**
 * What `POST /guided` is asked to do. The composer text travels only for an action the
 * host said takes it; `requiresText` is a UI hint the route does not check (contract
 * 1.3.3), so an empty composer still sends the bare action.
 */
export function guidedRequestFor(
  panel: StylePanel,
  actionId: string,
  text: string,
): GuidedRequest {
  const action = panel.actions.find((entry) => entry.id === actionId);
  const trimmed = text.trim();
  if (!action?.takesText || trimmed.length === 0) return { styleId: panel.style.id, actionId };
  return { styleId: panel.style.id, actionId, text: trimmed };
}
