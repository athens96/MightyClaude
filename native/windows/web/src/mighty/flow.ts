// The role view of a Claude pane's flow (docs/mighty-mode.md "역할 보기"), the page's side: the rules the layout and the drawing read from
// the runs the page holds, ported from Core FlowCard/FlowLines/FlowLog/FlowRoles/FlowSteps (macOS MightyCore of the same names). Every
// word and model name comes from the host's copy (Core FlowCopy, `MightyBlockCopy.flow`); what is here is what the page decides from its own
// drawing and its own clock: which block is a card, where the delegate lines, the steps and the plan column stand, which edges are live, how a
// card's time reads now, which chips fit a header. Pure.
import type { Translate } from '../i18n/locale';
import type { FlowStep, LogEntry, MightyGraphAgent, MightyGraphRun } from '../store/models';
import type { FlowCardCopy, FlowRoleCopy } from './types';
import { newestRequestIndex } from './notice';

// ── sizes (Core FlowCard, FlowDelegation, FlowSteps, MightyGraphLayout) ──
/** A sub-agent's or a background command's card. */
export const CardWidth = 236;
export const CardHeight = 86;
/** A card beside a request's step: one more line, the step it was launched in (Core/macOS FlowCard.stepSize). */
export const StepCardHeight = 104;
/** The 실행 card and a step's card: as wide as the request above them (Core/macOS FlowSteps.cardSize). */
export const WideCardWidth = 500;
export const DelegateWidth = 340;
export const DelegateHeight = 24;
export const DelegateGap = 18;
export const DelegateRowGap = 26;
export const DelegateKind = 'delegate';
export const DelegateSuffix = 'delegate:';
export const MainKey = 'main';
/** A request's step on its spine (macOS FlowSteps, MightyGraphLayout.Content.step). */
export const StepKind = 'step';
export const StepSuffix = 'step:';
/** From a spine block's trailing edge to the column of what it launched. */
export const SideGap = 44;
/** The progress panel over the role view's top-left corner (macOS MightyGraphProgressPanel, FlowSteps.progress). */
export const ProgressWidth = 280;
export const ProgressRowHeight = 24;
/** The rows an open log strip keeps room for (Core FlowLog.VisibleRows). */
export const LogRows = 5;
/** The log strip's header (its status line) and its open rows, in px (mighty.css: .mighty-log-head, --log-row, .mighty-log-rows' padding). */
export const LogHeadHeight = 27;
export const LogRowsHeight = LogRows * 18 + 5;
/** What the diagram's own view keeps of the pane's flow before the log strip shows its rows. */
export const LogDiagramRoom = 160;

/**
 * Whether the log strip shows its rows in a flow `room` px high (the diagram's view and the strip under it): only while the diagram still keeps
 * {@link LogDiagramRoom} beside them. A shorter pane shows the strip's status line alone, whatever the pane's fold, so the log never takes the
 * diagram's whole height (a view 0 px high draws every block as off screen). An unmeasured flow (0) shows them.
 */
export function logRowsFit(room: number): boolean {
  return room <= 0 || room >= LogHeadHeight + LogRowsHeight + LogDiagramRoom;
}

const ended = (status: string) =>
  status === 'completed' || status === 'error' || status === 'failed' || status === 'stopped' || status === 'cancelled' || status === 'interrupted';

/** The kind of block a core block is drawn as (Core MightyGraphSupport.BlockKind). */
export const blockKind = (agent: MightyGraphAgent) =>
  agent.kind === 'task' || agent.kind === 'steer' || agent.kind === 'compact' || agent.kind === 'question' ? agent.kind : 'agent';
export const isTask = (agent: MightyGraphAgent) => agent.kind === 'task';
/** A sub-agent: none of the kinds the core records apart (Core MightyGraphAgent.IsSubagent). */
export const isSubagent = (agent: MightyGraphAgent) => blockKind(agent) === 'agent';

/** A run as the views draw it, with what the role view still draws of the background blocks the other views leave out (notice.ts shownRun). */
export interface FlowRun extends MightyGraphRun {
  /** The background sub-agents and commands, and the blocks under them, that shownRun took out of `agents`, in the order they stood. */
  fanOut?: MightyGraphAgent[];
  /** Where each of flowAgents stood among the run's blocks as recorded. */
  flowOrder?: number[];
}

/** Every block the role view draws for a run: the ones every view draws, then the background ones (Core MightyGraphRun.FlowAgents). */
export const flowAgents = (run: MightyGraphRun): MightyGraphAgent[] => {
  const fan = (run as FlowRun).fanOut;
  return fan && fan.length > 0 ? [...run.agents, ...fan] : run.agents;
};

/** The place flowAgents[index] had among the run's blocks as recorded (Core MightyGraphRun.FlowOrder). */
export const flowOrder = (run: MightyGraphRun, index: number): number => (run as FlowRun).flowOrder?.[index] ?? index;

/** Whether the block has a card to fold back to: every sub-agent and background command, running or ended, a notice run's copy too
 * (Core/macOS FlowCard.FoldsToCard). Only the result is drawn whole. */
export const foldsToCard = (agent: MightyGraphAgent): boolean => isTask(agent) || isSubagent(agent);

/** Whether the role view draws the block as its card: it folds to one and the user did not open it (Core FlowCard.IsCompact). */
export const isCompact = (agent: MightyGraphAgent, opened: boolean) => foldsToCard(agent) && !opened;

/** The sub-agents and background commands block `key` (MainKey, or a sub-agent's id) of `run` hands work to, in launch order (Core FlowDelegation.Children). */
export function delegationChildren(key: string, run: MightyGraphRun): MightyGraphAgent[] {
  if (run.noticeRun === true) return [];
  const agents = flowAgents(run);
  const ids = new Set(agents.map((a) => a.id));
  return agents.map((agent, index) => ({ agent, order: flowOrder(run, index) }))
    .sort((a, b) => a.order - b.order)
    .map(({ agent }) => agent)
    .filter((agent) => {
      if (!(isSubagent(agent) || isTask(agent)) || (agent.sourceBlockId !== undefined && agent.sourceBlockId !== null)) return false;
      if (key === MainKey) return agent.parentID === undefined || agent.parentID === null || !ids.has(agent.parentID) || agent.parentID === agent.id;
      return agent.parentID === key && agent.id !== key;
    });
}

/** The block that stands for `nodeId` on the diagram: itself when drawn, else the nearest drawn block above it, else the run's 실행 block (Core FlowLog.DrawnNode). */
export function drawnNode(nodeId: string, run: MightyGraphRun, node: (suffix: string) => string, drawn: (id: string) => boolean): string | null {
  if (drawn(nodeId)) return nodeId;
  const agents = flowAgents(run);
  const own = agents.find((a) => node('agent:' + a.id) === nodeId);
  if (!own) return null;
  let above = own.parentID;
  for (let depth = 0; above !== undefined && above !== null && depth < 64; depth++) {
    const id = above;
    const parent = agents.find((a) => a.id === id);
    if (!parent) break;
    const candidate = node('agent:' + parent.id);
    if (drawn(candidate)) return candidate;
    above = parent.parentID;
  }
  for (const suffix of ['execution', 'result', 'request']) { const id = node(suffix); if (drawn(id)) return id; }
  return null;
}

/** Where a log row's click goes among the newest activity's runs (Core FlowLog.Target). */
export function logTarget(nodeId: string, runs: readonly MightyGraphRun[], node: (run: MightyGraphRun, suffix: string) => string, drawn: (id: string) => boolean): string | null {
  if (drawn(nodeId)) return nodeId;
  if (runs.length === 0) return null;
  const start = newestRequestIndex(runs) ?? runs.length - 1;
  for (let index = runs.length - 1; index >= start; index--) {
    const run = runs[index]!;
    // The role view draws the 실행 block only until the answer is there: then the main session is its result (macOS FlowLog.target).
    if (nodeId === node(run, 'execution')) {
      for (const suffix of ['result', 'request']) { const id = node(run, suffix); if (drawn(id)) return id; }
      return null;
    }
    const found = drawnNode(nodeId, run, (suffix) => node(run, suffix), drawn);
    if (found) return found;
    if (flowAgents(run).some((a) => node(run, 'agent:' + a.id) === nodeId)) return null;
  }
  return null;
}

// ── a card's time, read against the page's own clock ──

/** `45초`, `3분 12초`, `1시간 2분`; a clock that went back reads as 0 (Core StylePresentation.Elapsed). */
export function elapsed(t: Translate, fromMs: number, toMs: number): string {
  const seconds = Math.max(0, Math.floor((toMs - fromMs) / 1000));
  if (seconds < 60) return t('styles.state.elapsedSeconds', { seconds });
  if (seconds < 3600) return t('styles.state.elapsedMinutes', { minutes: Math.floor(seconds / 60), seconds: seconds % 60 });
  return t('styles.state.elapsedHours', { hours: Math.floor(seconds / 3600), minutes: Math.floor((seconds % 3600) / 60) });
}

/** The card's time: since it started while it runs, how long it ran once it ended; null when the app did not see it (Core FlowCard.Elapsed). */
export function cardTime(t: Translate, card: FlowCardCopy, now: number): string | null {
  if (card.startedAt === undefined) return null;
  if (card.running) return elapsed(t, card.startedAt, now);
  return card.endedAt !== undefined && card.endedAt >= card.startedAt ? elapsed(t, card.startedAt, card.endedAt) : null;
}

/** The card's last line: its state (a command's exit code), its time, its tools and tokens (Core FlowCard.Figures). */
export function cardFigures(t: Translate, card: FlowCardCopy, now: number): string {
  const time = cardTime(t, card, now);
  return [...card.before, ...(time === null ? [] : [time]), ...card.after].join(' · ');
}

// ── the header's chips (Core FlowRoles.FitHeader) ──

export const TitleFloor = 72;
export const TypeFloor = 40;
export const TypeCeiling = 110;
export const MarkWidth = 14;
const Gap = 6;
const ChipPadding = 2 * Gap;
const BodyInset = 8;

/** The width a short label takes at `size` points: a wide (Hangul, kana, Han) character a full em, any other a little over half. */
export function estimatedWidth(text: string, size: number, monospaced = false): number {
  let width = 0;
  for (const char of text) {
    const value = char.codePointAt(0) ?? 0;
    const wide = (value >= 0x1100 && value <= 0x11ff) || (value >= 0x2e80 && value <= 0xa4cf) || (value >= 0xac00 && value <= 0xd7a3)
      || (value >= 0xf900 && value <= 0xfaff) || (value >= 0xff00 && value <= 0xffef);
    width += size * (wide ? 1 : monospaced ? 0.61 : 0.56);
  }
  return width;
}

export interface HeaderFit { typeWidth: number | null; showsEffort: boolean; showsMarks: boolean }

/** What fits in a block header `width` wide beside what the header always showed: the marks, then the effort, then the type chip, which takes the
 * width given here or is left out. At the block's least width with a status and a capsule none of them shows (Core FlowRoles.FitHeader). */
export function headerFit(width: number, title: string, status: string | null, capsule: string | null, buttons: number, role: FlowRoleCopy): HeaderFit {
  let used = 2 * BodyInset + 16 + Gap;
  used += Math.min(TitleFloor, estimatedWidth(title, 12)) + Gap;
  if (status !== null) used += 18 + Gap + estimatedWidth(status, 10) + ChipPadding + Gap;
  if (capsule !== null) used += estimatedWidth(capsule, 10, true) + ChipPadding + Gap;
  used += buttons * (16 + Gap);
  let free = width - used;
  const fit: HeaderFit = { typeWidth: null, showsEffort: false, showsMarks: false };
  if (role.marks.length > 0) {
    const marks = role.marks.length * MarkWidth + Gap;
    if (free >= marks) { fit.showsMarks = true; free -= marks; }
  }
  if (role.effort !== undefined) {
    const chip = MarkWidth + estimatedWidth(role.effort, 10) + Gap;
    if (free >= chip) { fit.showsEffort = true; free -= chip; }
  }
  if (role.typeChip !== undefined && free >= TypeFloor + Gap) {
    const wanted = Math.ceil(estimatedWidth(role.typeChip, 10) * 1.15 + ChipPadding + 4);
    fit.typeWidth = Math.max(TypeFloor, Math.min(TypeCeiling, free - Gap, wanted));
  }
  return fit;
}

/** The ids whose incoming edges are live (blue): running blocks and a delegate line that leads to a running block (macOS
 * MightyGraphLayout.liveFlowIDs). */
export function liveFlowIds(layout: { nodes: readonly { id: string; kind: string }[]; edges: readonly { source: string; target: string }[] }, running: ReadonlySet<string>): Set<string> {
  const ids = new Set(running);
  const lines = new Set(layout.nodes.filter((n) => n.kind === DelegateKind).map((n) => n.id));
  for (const edge of layout.edges) if (lines.has(edge.source) && running.has(edge.target)) ids.add(edge.source);
  return ids;
}


// ── a request's steps (macOS FlowSteps) ──

/** FNV-1a, 64 bits, in hex (lowercase, no leading zeros): the same as the Mac's and the Core's. */
export function stableHash(text: string): string {
  let hash = 0xcbf29ce484222325n;
  const prime = 0x100000001b3n;
  for (const byte of new TextEncoder().encode(text)) {
    hash ^= BigInt(byte);
    hash = (hash * prime) & 0xffffffffffffffffn;
  }
  return hash.toString(16);
}

/** A step block's node suffix: `step:` and a hash of its key, so a node id never carries the item's words. */
export const stepSuffix = (step: Pick<FlowStep, 'key'>) => StepSuffix + stableHash(step.key);

/** The steps a run draws: its own, never a notice run's. */
export const runSteps = (run: MightyGraphRun): FlowStep[] => (run.noticeRun === true ? [] : run.steps ?? []);

const time = (stamp: string | undefined | null): number | null => {
  if (!stamp) return null;
  const value = Date.parse(stamp);
  return Number.isFinite(value) ? value : null;
};
export const stepStarted = (step: FlowStep) => time(step.startedAt);
export const stepEnded = (step: FlowStep) => time(step.endedAt);
export const isPendingStep = (step: FlowStep) => step.status === 'pending';

/** The step whose span holds `at` (ms), the one that started last where spans meet; null for the main session's own time between steps. A step's
 * start counts from its whole second, as the records' times do (macOS FlowSteps.stepIndex). */
export function stepIndex(at: number, steps: readonly FlowStep[]): number | null {
  let best: { index: number; start: number } | null = null;
  steps.forEach((step, index) => {
    const start = stepStarted(step);
    if (start === null || Math.floor(start / 1000) * 1000 > at) return;
    const end = stepEnded(step);
    if (end !== null && end < at) return;
    if (best === null || start >= best.start) best = { index, start };
  });
  return (best as { index: number } | null)?.index ?? null;
}

/** Where on the spine a block launched from the main session stands: 0 beside the request, k beside step k (macOS FlowSteps.slot). */
export function stepSlot(agent: MightyGraphAgent, steps: readonly FlowStep[]): number {
  const start = time(agent.figures?.startedAt);
  if (start === null) return 0;
  const index = stepIndex(start, steps);
  return index === null ? 0 : index + 1;
}

/** Whether `agent`, a block of `run`, was launched from the main session inside one of the request's steps that began: its card names the step
 * on a line of its own (Core/macOS FlowSteps.StepLabel is not null). */
export function launchedInStep(agent: MightyGraphAgent, run: MightyGraphRun, agents: readonly MightyGraphAgent[]): boolean {
  if (agent.parentID != null && agent.parentID !== agent.id && agents.some((a) => a.id === agent.parentID)) return false;
  const started = runSteps(run).filter((step) => !isPendingStep(step));
  return started.length > 0 && stepSlot(agent, started) > 0;
}

/** One request's rows on the diagram: from its request block to its last block, a notice run's counted as its request's. */
export interface RequestSpan { id: string; top: number; bottom: number }

/** Each request's rows (macOS MightyGraphView.requestSpans). `runIndexOf` names the run a node is about (null for none of them). */
export function requestSpans(nodes: readonly { id: string; frame: { y: number; h: number } }[], runs: readonly MightyGraphRun[],
  runIndexOf: (nodeId: string) => number | null): RequestSpan[] {
  const owner: number[] = [];
  let current: number | null = null;
  runs.forEach((run, index) => {
    if (run.noticeRun !== true || current === null) current = index;
    owner.push(current);
  });
  const spans = new Map<number, { top: number; bottom: number }>();
  for (const node of nodes) {
    const index = runIndexOf(node.id);
    if (index === null || owner[index] === undefined) continue;
    const at = owner[index]!;
    const span = spans.get(at) ?? { top: node.frame.y, bottom: node.frame.y + node.frame.h };
    spans.set(at, { top: Math.min(span.top, node.frame.y), bottom: Math.max(span.bottom, node.frame.y + node.frame.h) });
  }
  return [...spans.keys()].sort((a, b) => a - b).map((at) => ({ id: runs[at]!.id, ...spans.get(at)! }));
}

/** The request a view from `top`, `height` high (diagram coordinates), is about: the one whose rows cross a third of the way down, else the
 * nearest (macOS MightyGraphView.focus). */
export function focusedRequest(spans: readonly RequestSpan[], top: number, height: number): string | null {
  const line = top + height / 3;
  const hit = [...spans].reverse().find((s) => s.top <= line && line <= s.bottom);
  if (hit) return hit.id;
  let best: RequestSpan | null = null, distance = Infinity;
  for (const span of spans) {
    const d = Math.min(Math.abs(span.top - line), Math.abs(span.bottom - line));
    if (d < distance) { distance = d; best = span; }
  }
  return best?.id ?? null;
}

/** Which request the progress panel shows: a new request it was pinned to (until the user scrolls), else the one in view, else the newest. */
export function progressRequest(pinned: string | null, focused: string | null, newest: string | null, has: (id: string) => boolean): string | null {
  if (pinned !== null && has(pinned)) return pinned;
  if (focused !== null && has(focused)) return focused;
  return newest !== null && has(newest) ? newest : null;
}

/** The record ids a step holds, among `entries` (the activity's main-session records). */
export function stepEntries(step: number, steps: readonly FlowStep[], entries: readonly LogEntry[]): LogEntry[] {
  return entries.filter((entry) => {
    const at = time(entry.timestamp);
    return at !== null && stepIndex(at, steps) === step;
  });
}
