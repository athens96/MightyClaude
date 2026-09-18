import {
  MAX_BLOCK_OUTPUT,
  type GuidedRequest,
  type GuidedStyle,
  type MobileBlock,
  type MobileMighty,
  type MobileMightyRun,
  type MobileOuroboros,
  type MobilePaperthin,
  type OuroborosAction,
  type PaperthinDomain,
  type PaperthinSkill,
} from '@/api/types';

/**
 * The block list the phone shows instead of the Mac's graph. Everything here is pure:
 * the host's payload is read defensively (a field it adds later, or one it leaves out,
 * must never crash a screen) and turned into labels the list can draw.
 */

/** The Mac's own names for the six block kinds it draws. */
export const BLOCK_KIND_LABELS: Record<string, string> = {
  main: '요청',
  agent: '하위 에이전트',
  task: '백그라운드 작업',
  steer: '중간 요청',
  compact: '컨텍스트 정리',
  question: '질문',
};

/** Marks drawn in place of the Mac's SF Symbols; this app has no vector renderer. */
export const BLOCK_KIND_MARKS: Record<string, string> = {
  main: '●',
  agent: '◆',
  task: '▣',
  steer: '↳',
  compact: '⤡',
  question: '?',
};

/** Neutral mark for a kind the contract does not list. */
export const UNKNOWN_BLOCK_MARK = '○';

/** Longest run input preview kept; the full text is in the transcript. */
export const MAX_INPUT_PREVIEW = 120;
/** Most runs kept from one payload; the contract says the host sends 20. */
export const MAX_RUNS = 20;
/**
 * Most blocks kept per run. A long Ouroboros run can carry hundreds, and every one of
 * them is a mounted row; the newest are the ones worth drawing, and the count of the
 * rest is shown instead so nothing disappears silently.
 */
export const MAX_BLOCKS_PER_RUN = 200;

/**
 * C0/C1 controls and the bidi overrides, which can reorder what a row appears to say.
 * Newline and tab survive in block output, which is shown as a monospace excerpt.
 */
const UNSAFE_INLINE = /[\u0000-\u001f\u007f-\u009f\u200e\u200f\u202a-\u202e\u2066-\u2069]/g;
const UNSAFE_BLOCK =
  /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f\u200e\u200f\u202a-\u202e\u2066-\u2069]/g;

function inlineText(value: unknown, limit: number): string {
  if (typeof value !== 'string') return '';
  return value.replace(UNSAFE_INLINE, '').trim().slice(0, limit);
}

function blockText(value: unknown, limit: number): string {
  if (typeof value !== 'string') return '';
  return value.replace(UNSAFE_BLOCK, '').slice(0, limit);
}

/** The label for a block kind, or the word the host sent when we do not know it. */
export function blockKindLabel(kind: string): string {
  return BLOCK_KIND_LABELS[kind] ?? (kind.length > 0 ? kind : '블록');
}

/** The mark for a block kind; an unknown kind gets the neutral one. */
export function blockKindMark(kind: string): string {
  return BLOCK_KIND_MARKS[kind] ?? UNKNOWN_BLOCK_MARK;
}

/** The block's own title, falling back to what its kind is called. */
export function blockTitle(block: MobileBlock): string {
  const title = block.title.trim();
  return title.length > 0 ? title : blockKindLabel(block.kind);
}

/** `<제목> · 요청 3`, or just `요청 3` when the run has no title. */
export function runHeading(run: MobileMightyRun, index: number): string {
  const title = run.title?.trim() ?? '';
  return `${title.length > 0 ? `${title} · ` : ''}요청 ${index + 1}`;
}

/** One line of the request, for the collapsed group. */
export function runPreview(input: string): string {
  const flat = input.replace(UNSAFE_INLINE, ' ').replace(/\s+/g, ' ').trim();
  if (flat.length <= MAX_INPUT_PREVIEW) return flat;
  return `${flat.slice(0, MAX_INPUT_PREVIEW)}…`;
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

/**
 * What `POST /guided` is asked to do. Ouroboros carries the composer text only for the
 * skills the host listed in `takesText`; Paperthin always passes it, because the text is
 * the skill's target (`/re0 docs/spec.md`).
 */
export function guidedRequestFor(
  style: GuidedStyle,
  skill: string,
  text: string,
  takesText: readonly string[],
): GuidedRequest {
  const carries = style === 'paperthin' || takesText.includes(skill);
  const trimmed = text.trim();
  if (!carries || trimmed.length === 0) return { style, skill };
  return { style, skill, text: trimmed };
}

// ------------------------------------------------------------------ parsing

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function arrayOf(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function stringList(value: unknown): string[] {
  return arrayOf(value)
    .filter((entry): entry is string => typeof entry === 'string' && entry.length > 0);
}

function parseBlock(raw: unknown): MobileBlock | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const block: MobileBlock = {
    id: raw.id,
    kind: inlineText(raw.kind, 40),
    title: inlineText(raw.title, 160),
    status: inlineText(raw.status, 40),
  };
  const summary = inlineText(raw.summary, 400);
  if (summary.length > 0) block.summary = summary;
  const output = blockText(raw.output, MAX_BLOCK_OUTPUT);
  if (output.length > 0) block.output = output;
  if (typeof raw.durationMs === 'number' && Number.isFinite(raw.durationMs) && raw.durationMs >= 0) {
    block.durationMs = raw.durationMs;
  }
  return block;
}

function parseRun(raw: unknown): MobileMightyRun | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const parsed = arrayOf(raw.blocks)
    .map(parseBlock)
    .filter((block): block is MobileBlock => block !== undefined);
  const run: MobileMightyRun = {
    id: raw.id,
    input: typeof raw.input === 'string' ? raw.input : '',
    status: inlineText(raw.status, 40),
    blocks: parsed.slice(-MAX_BLOCKS_PER_RUN),
  };
  if (parsed.length > MAX_BLOCKS_PER_RUN) {
    run.omittedBlocks = parsed.length - MAX_BLOCKS_PER_RUN;
  }
  const title = inlineText(raw.title, 160);
  if (title.length > 0) run.title = title;
  return run;
}

function parseAction(raw: unknown): OuroborosAction | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.skill !== 'string' || raw.skill.length === 0) return undefined;
  const title = inlineText(raw.title, 80);
  return {
    skill: raw.skill,
    title: title.length > 0 ? title : raw.skill,
    help: inlineText(raw.help, 400),
  };
}

function parseOuroboros(raw: unknown): MobileOuroboros | undefined {
  if (!isRecord(raw)) return undefined;
  const actions = (value: unknown) =>
    arrayOf(value)
      .map(parseAction)
      .filter((action): action is OuroborosAction => action !== undefined);
  return {
    phase: inlineText(raw.phase, 40),
    ready: raw.ready !== false,
    takesText: stringList(raw.takesText),
    next: actions(raw.next),
    all: actions(raw.all),
  };
}

function parseSkill(raw: unknown): PaperthinSkill | undefined {
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

function parseDomain(raw: unknown): PaperthinDomain | undefined {
  if (!isRecord(raw)) return undefined;
  if (typeof raw.id !== 'string' || raw.id.length === 0) return undefined;
  const title = inlineText(raw.title, 40);
  return {
    id: raw.id,
    title: title.length > 0 ? title : raw.id,
    axis: inlineText(raw.axis, 60),
    question: inlineText(raw.question, 200),
    skills: arrayOf(raw.skills)
      .map(parseSkill)
      .filter((skill): skill is PaperthinSkill => skill !== undefined),
  };
}

function parsePaperthin(raw: unknown): MobilePaperthin | undefined {
  if (!isRecord(raw)) return undefined;
  const paperthin: MobilePaperthin = {
    installed: raw.installed !== false,
    domains: arrayOf(raw.domains)
      .map(parseDomain)
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

/**
 * Reads `MobileSessionDetail.mighty`. Returns undefined when the host sent nothing we
 * can draw; otherwise every list is present (possibly empty) and every string is safe
 * to render.
 */
export function normalizeMighty(raw: unknown): MobileMighty | undefined {
  if (!isRecord(raw)) return undefined;
  const mighty: MobileMighty = {
    style: inlineText(raw.style, 40),
    runs: arrayOf(raw.runs)
      .map(parseRun)
      .filter((run): run is MobileMightyRun => run !== undefined)
      .slice(-MAX_RUNS),
  };
  const ouroboros = parseOuroboros(raw.ouroboros);
  if (ouroboros) mighty.ouroboros = ouroboros;
  const paperthin = parsePaperthin(raw.paperthin);
  if (paperthin) mighty.paperthin = paperthin;
  return mighty;
}

/** The guided style in effect, or undefined for the plain CLI style. */
export function guidedStyleOf(mighty: MobileMighty | undefined): GuidedStyle | undefined {
  if (mighty?.style === 'ouroboros') return 'ouroboros';
  if (mighty?.style === 'paperthin') return 'paperthin';
  return undefined;
}
