// The role view of a Claude pane's flow (docs/mighty-mode.md "역할 보기"), the page's side: the rules the layout and the drawing read from
// the runs the page holds, ported from Core FlowCard/FlowLines/FlowAdvisor/FlowLog/FlowRoles (macOS MightyCore of the same names). Every
// word and model name comes from the host's copy (Core FlowCopy, `MightyBlockCopy.flow`); what is here is what the page decides from its own
// drawing and its own clock: which block is a card, where the delegate lines, the return node and the advisor's band stand, which edges are
// live, how a card's time reads now, which chips fit a header, which advisor rows fit a band. Pure.
import type { Translate } from '../i18n/locale';
import type { MightyGraphAgent, MightyGraphRun } from '../store/models';
import type { FlowAdvisorCopy, FlowCardCopy, FlowRoleCopy } from './types';
import { newestRequestIndex } from './notice';

// ── sizes (Core FlowCard, FlowDelegation, FlowReturn, FlowAdvisor, MightyGraphLayout) ──
export const CardWidth = 236;
export const CardHeight = 86;
export const DelegateWidth = 340;
export const DelegateHeight = 24;
export const ReturnWidth = 340;
export const ReturnHeight = 34;
export const DelegateGap = 18;
export const DelegateRowGap = 26;
export const DelegateKind = 'delegate';
export const DelegateSuffix = 'delegate:';
export const ReturnKind = 'return';
export const ReturnSuffix = 'return';
export const AdvisorKind = 'advisor';
export const AdvisorSuffix = 'advisor';
export const MainKey = 'main';
export const AdvisorWidth = 248;
export const AdvisorGap = 28;
export const AdvisorHeaderHeight = 28;
export const AdvisorRowHeight = 38;
export const AdvisorEarlierHeight = 18;
export const AdvisorNoteHeight = 32;
export const AdvisorFooterHeight = 24;
export const AdvisorMinimumHeight = AdvisorHeaderHeight + AdvisorEarlierHeight + AdvisorRowHeight + AdvisorNoteHeight + AdvisorFooterHeight;
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

/** Whether the block has a card to fold back to: a request's own background sub-agent or command at any time, a foreground sub-agent while it runs. */
export function foldsToCard(agent: MightyGraphAgent): boolean {
  if (agent.sourceBlockId !== undefined && agent.sourceBlockId !== null) return false;
  if (isTask(agent)) return true;
  if (!isSubagent(agent)) return false;
  return agent.background === true || !ended(agent.status);
}

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

/** Whether a run has its way back: it hands work to a sub-agent or a background command and is no notice run (Core FlowReturn.Of is not null). */
export const hasReturn = (run: MightyGraphRun) =>
  run.noticeRun !== true && flowAgents(run).some((a) => (isSubagent(a) || isTask(a)) && (a.sourceBlockId === undefined || a.sourceBlockId === null));

/** Whether a run has the advisor's band: its stream showed an advisor at work, or it is a request started with one on (Core FlowAdvisor.Shows). */
export const advisorShows = (run: MightyGraphRun) =>
  (run.provider ?? 'claude') === 'claude' && (run.advisor !== undefined || (run.configuredAdvisor !== undefined && run.noticeRun !== true));

/** The blocks that consulted the advisor, each once, in the order they first asked (Core FlowAdvisor.Callers). */
export function advisorCallers(run: MightyGraphRun, node: (suffix: string) => string): string[] {
  const agents = flowAgents(run);
  const seen = new Set<string>();
  const result: string[] = [];
  for (const call of run.advisor?.calls ?? []) {
    const agent = call.caller ? agents.find((a) => a.id === call.caller) : undefined;
    const id = node(agent ? 'agent:' + agent.id : 'execution');
    if (!seen.has(id)) { seen.add(id); result.push(id); }
  }
  return result;
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
  const work = node('execution');
  return drawn(work) ? work : null;
}

/** Where a log row's click goes among the newest activity's runs (Core FlowLog.Target). */
export function logTarget(nodeId: string, runs: readonly MightyGraphRun[], node: (run: MightyGraphRun, suffix: string) => string, drawn: (id: string) => boolean): string | null {
  if (drawn(nodeId)) return nodeId;
  if (runs.length === 0) return null;
  const start = newestRequestIndex(runs) ?? runs.length - 1;
  for (let index = runs.length - 1; index >= start; index--) {
    const run = runs[index]!;
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

// ── the advisor's band ──

/** The rows a band of `height` shows, the newest last, and how many stand before them (Core FlowAdvisor.Visible). */
export function advisorVisible(band: FlowAdvisorCopy, height: number): { rows: FlowAdvisorCopy['rows']; earlier: number } {
  const room = height - AdvisorHeaderHeight - AdvisorFooterHeight - (band.hidesText ? AdvisorNoteHeight : 0);
  const whole = Math.max(0, Math.floor(room / AdvisorRowHeight));
  if (band.earlier === 0 && band.rows.length <= whole) return { rows: band.rows, earlier: 0 };
  const shown = Math.max(0, Math.floor((room - AdvisorEarlierHeight) / AdvisorRowHeight));
  const rows = shown === 0 ? [] : band.rows.slice(-shown);
  return { rows, earlier: band.calls - rows.length };
}

export interface Box { x: number; y: number; w: number; h: number }
const intersects = (a: Box, b: Box) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;

/** From a band's trailing edge to the block that asked: straight at the block header's height when nothing stands between them, else along the gap
 * above the block's row and down into its top by its leading corner (Core/macOS MightyGraphLayout.advisorRoute). */
export function advisorRoute(band: Box, target: Box, others: readonly Box[]): { x: number; y: number }[] {
  const inset = 8;
  const onBand = (y: number) => Math.min(Math.max(y, band.y + inset), Math.max(band.y + inset, band.y + band.h - inset));
  const level = target.y + AdvisorHeaderHeight / 2;
  const path = { x: band.x + band.w, y: level - 1, w: Math.max(0, target.x - (band.x + band.w)), h: 2 };
  if (level === onBand(level) && level <= target.y + target.h && !others.some((o) => intersects(o, path))) {
    return [{ x: band.x + band.w, y: level }, { x: target.x, y: level }];
  }
  const lane = target.y - 6, start = onBand(lane), corner = target.x + 14;
  const points = [{ x: band.x + band.w, y: start }];
  if (start !== lane) {
    const step = band.x + band.w + AdvisorGap / 2;
    points.push({ x: step, y: start }, { x: step, y: lane });
  }
  return [...points, { x: corner, y: lane }, { x: corner, y: target.y }];
}

/** The ids whose incoming edges are live (blue): running blocks, every return node (it stands only while its run goes on) and a delegate line that
 * leads to a running block (macOS MightyGraphLayout.liveFlowIDs). */
export function liveFlowIds(layout: { nodes: readonly { id: string; kind: string }[]; edges: readonly { source: string; target: string }[] }, running: ReadonlySet<string>): Set<string> {
  const ids = new Set(running);
  for (const node of layout.nodes) if (node.kind === ReturnKind) ids.add(node.id);
  const lines = new Set(layout.nodes.filter((n) => n.kind === DelegateKind).map((n) => n.id));
  for (const edge of layout.edges) if (lines.has(edge.source) && running.has(edge.target)) ids.add(edge.source);
  return ids;
}

