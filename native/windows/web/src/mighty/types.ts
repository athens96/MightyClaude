// What the host pushes for a pane's Mighty view (MainWindow.WebShell.Mighty.cs): the copy is the host's (Locale, model
// names, Core's catalogues); the frames are the page's own layout (layout.ts).
import type { MightyGraphRun } from '../store/models';

/** One diagram block's copy (MightyGraphBlockModel.Blocks): no frame, no transcript. */
export interface MightyBlockCopy {
  kind: string;
  title: string;
  state: string;
  capsule?: string;
  help: string;
  indicator: string;
  outline: string;
  status: string;
  tone: string;
  filesRunId?: string;
  recordId?: string;
  fromRecord: boolean;
  provider?: string;
  /** A background sub-agent's or a task's block. */
  background?: boolean;
  /** A task block's last output lines (at most 6), read on the host. */
  outputTail?: string[];
  /** Claude's reply to a task's notice, when that notice alone opened its turn. */
  noticeReply?: string;
  /** A notice run's request block (notice.ts): titled 백그라운드 작업 완료, with the task's terminal symbol and no provider mark. */
  notice?: boolean;
  /** A result whose request awaits its background work's notices (Core MightyGraphBlock.Awaiting): the 알림 대기 block, its rows under the answer so far. */
  awaiting?: MightyAwaiting;
  /** A result that is the result so far (Core MightyGraphBlock.SoFar): its strip and symbol are neutral, its tone stays the request's. */
  soFar?: boolean;
  /** The role view's words for this node (Core FlowCopy.Block): who the block is, its card, a delegate line, a step or the plan column. */
  flow?: FlowBlockCopy;
}

/** A mark on a sub-agent's header (Core FlowMark): background, fork or worktree, and what it says on hover. */
export interface FlowMarkCopy { name: 'background' | 'fork' | 'worktree'; help: string }

/** Who a block is (Core FlowCopy.Role): its role, its ink (a design token's name), its type chip, the lead's effort, its marks, all of it in one line. */
export interface FlowRoleCopy {
  name: 'lead' | 'subagent' | 'task';
  ink: string;
  type?: string;
  typeChip?: string;
  effort?: string;
  effortHelp?: string;
  marks: FlowMarkCopy[];
  summary: string;
  modelLabel?: string;
}

/** A compact card (Core FlowCopy.Card): the figures' parts on either side of its time, which the page reads against its own clock. */
export interface FlowCardCopy {
  title: string;
  identity?: string;
  activity?: string;
  status: string;
  stateWord: string;
  before: string[];
  after: string[];
  /** Milliseconds since 1970. */
  startedAt?: number;
  endedAt?: number;
  running: boolean;
  nested: number;
  nestedHelp?: string;
  marks: FlowMarkCopy[];
  ink: string;
  summary: string;
  isTask: boolean;
  /** A finished background block's full copy in its notice run: where its card's click goes. */
  copyNodeId?: string;
  activateHelp: string;
  /** The activity is what the main session said (the 실행 card, a step's card): set as text, not as a command. */
  prose?: boolean;
  /** A step not begun: no time runs and nothing on it moves. */
  pending?: boolean;
  /** The step of the request's plan it was launched in ("2. 노트 작성"), on a line of its own; `stepHelp` says it in a sentence. */
  step?: string;
  stepHelp?: string;
}

export interface FlowPartCopy { text: string; ink: string }
/** The delegate line (Core FlowDelegation.Line). */
export interface FlowLineCopy { head: string; parts: FlowPartCopy[]; model?: string; progress?: string; text: string }
/** A step of a request opened into its block (Core/macOS FlowSteps): its title, its state, and the main session's records during it, by id. */
export interface FlowStepCopy { title: string; stateWord: string; status: string; entryIds: string[] }
export type FlowProgressState = 'done' | 'current' | 'pending' | 'stopped' | 'failed';
export interface FlowProgressRowCopy {
  /** The step's index among the request's steps. */
  index: number;
  /** The step's card on the spine, when it began. */
  nodeId?: string;
  text: string;
  state: FlowProgressState;
  /** Milliseconds since 1970. */
  startedAt?: number;
  endedAt?: number;
}
/** A request's progress panel (Core FlowCopy.ProgressCopy, macOS FlowSteps.Progress): "진행 상황", "요청 27", its first line and state, and its plan. */
export interface FlowProgressCopy {
  title: string;
  heading: string;
  request: string;
  stateWord: string;
  /** The request's state bucket, or "waiting" while it awaits its notices. */
  status: string;
  /** "3/9"; absent for a request without a plan. */
  count?: string;
  rows: FlowProgressRowCopy[];
  hidden: number;
  more?: string;
  running: boolean;
  /** Where the request line leads, and what it says on hover. */
  requestNodeId: string;
  requestHelp: string;
}

export interface FlowBlockCopy {
  role?: FlowRoleCopy;
  card?: FlowCardCopy;
  line?: FlowLineCopy;
  step?: FlowStepCopy;
}

/** The session log strip under a Claude pane's flow (Core FlowCopy.Log). */
export interface FlowLogCopy {
  status: FlowPartCopy[];
  statusText: string;
  rows: { id: string; clock: string; who: string; ink: string; what: string; nodeId: string }[];
  collapsed: boolean;
}

/** The 알림 대기 block's rows (Core BackgroundNoticeRuns.Awaiting): each awaited task's title, status and word, and "알림 n/m". */
export interface MightyAwaiting {
  tasks: { title: string; status: string; word: string }[];
  progress: string;
}

export interface MightyFile { path: string; line?: number }

export interface MightyExecutionCard {
  status: string;
  statusWord: string;
  goal?: string;
  progress?: string;
  counts: string[];
  line?: string;
  note?: string;
  footer: string;
  dashboard?: string;
  /** The execution's key: what "open dashboard" names. */
  key: string;
  /** The AC list is open on the host (it reads the board only then). */
  listOpen: boolean;
  /** While the list is open with nothing to show: it is being read, or there is none. */
  listNote?: string;
  /** The first 300 acceptance criteria while the list is open. */
  acs?: { title: string; status: string; statusWord: string; depth: number }[];
  opening: boolean;
  openFailed: boolean;
  /** The share of the ACs done, 0–1, when the total is known. */
  done?: number;
}

export interface MightyInputs {
  planRunID?: string;
  planRecords: { runID: string; recordID: string }[];
  galleries: { runID: string; step: string; count: number }[];
  executions: { runID: string; key: string }[];
  resultFilesRunID?: string;
  files: Record<string, MightyFile[]>;
  older: number;
  retainedStart: number;
  history?: { text: string; acts: boolean; phase: string };
  summary: string;
  summaryHelp: string;
  executionCards: Record<string, MightyExecutionCard>;
  animations: boolean;
  header: string;
  /** The role view (a Claude pane): the request's steps or its row of sub-agents and background commands as cards, the main session's card where the
   * answer will come, and the plan column (Core MightyGraphLayout.FanOut). Absent elsewhere and from an older host. */
  fanOut?: boolean;
  /** The session log strip under the flow, for a role view with a request. */
  log?: FlowLogCopy;
  /** The progress panel's copy of each request, by its run id (Core FlowCopy.ProgressOf), in the role view. */
  progress?: Record<string, FlowProgressCopy>;
}

export interface TimelineRow {
  nodeId: string;
  kind: string;
  kindLabel: string;
  title: string;
  status: string;
  statusWord: string;
  tone: string;
  ring: boolean;
  rail?: string;
  latest?: string;
  duration?: string;
  capsule?: string;
  input?: string;
  agentIndex?: number;
  /** The block's role ink (a design token's name) as its diagram header wears it: the 실행 row's lead teal, a sub-agent's own (Core FlowRoles). */
  ink?: string;
}

export interface TimelineGroup {
  runId: string;
  ordinal: number;
  title: string;
  status: string;
  statusWord: string;
  tone: string;
  input: string;
  tally: string;
  open: boolean;
  older: boolean;
  rows: TimelineRow[];
  resultNodeId?: string;
  resultTone?: string;
  result?: string;
  resultFolds: boolean;
  resultCaption?: string;
  /** Claude's reply to finished background work (notice.ts), not a request: it takes no request number. */
  noticeRun?: boolean;
  /** The result head's title (Core MightyGraphBlockModel.ResultTitle): 알림 대기 중 while the request awaits its background work's notices,
   * else 현재까지의 결과 with the count while background work of its request runs. */
  resultTitle?: string;
}

export interface StructureCard {
  id: string;
  kind: string;
  runId: string;
  nodeId: string;
  title: string;
  status: string;
  statusWord: string;
  tone: string;
  parent?: string;
  text?: string;
  message?: string;
  answer?: string;
  activity: StructureActivity[];
  tally?: string;
  finished: boolean;
  resultLine?: string;
  /** An agent card's block kind ("task", "steer" for a task notice, an agent's), "kind · model", how deep it is nested, and what it was asked. */
  nodeKind?: string;
  meta?: string;
  depth?: number;
  prompt?: string;
  /** The 실행 block and the result block of the card's request, for the reveal buttons. */
  executionNodeId?: string;
  resultNodeId?: string;
  /** The card's state colour (Core MightyStructure.Tone.OfCard): run, wait, done, err or stop; none for a step still to come. */
  tint?: string | null;
  /** An agent card that is a background sub-agent or a task; its last output lines and Claude's reply to its notice, as on a block. */
  background?: boolean;
  outputTail?: string[];
  noticeReply?: string;
  /** A result or summary card's result title (Core MightyGraphBlockModel.ResultTitle): 알림 대기 중 while the request awaits notices, else
   * 현재까지의 결과 with the count while background work of its request runs. */
  resultTitle?: string;
  /** A result or summary card whose request awaits its background work's notices: drawn in the wait tone, not done. */
  resultAwaiting?: boolean;
  /** A result or summary card that is the result so far: drawn neutral, not done. */
  resultWaiting?: boolean;
}

/** One tool call on a card: its kind (command, read, edit, search, web, agent, turn, tool), its state and its words. */
export interface StructureActivity { kind: string; state: string; text: string }

export interface StructureColumn { id: string; title: string; step: string; stepWord: string; count: number; cards: StructureCard[]; /** The step's state colour (MightyStructure.Tone.OfColumn), none while it is to come. */ tone?: string | null }
export interface StructureBoard { id: string; kind: string; ordinal: number; headline?: string; status: string; statusWord: string; tone: string; runIds: string[]; columns: StructureColumn[]; styleName?: string;
  /** Only notice runs taken into a running turn (notice.ts quietNotice): the view never moves on to it by itself. */
  quiet?: boolean }

/** host → web: a pane's Mighty model, only what changed: blocks by id, and the other parts whole when they changed. */
export interface MightyMessage {
  kind: 'mighty';
  paneId: string;
  blocks?: Record<string, MightyBlockCopy>;
  removed?: string[];
  inputs?: MightyInputs;
  older?: MightyGraphRun[];
  timeline?: TimelineGroup[];
  structure?: StructureBoard[];
}

/** The page's copy of one pane's Mighty model; a block's copy that did not change keeps its object. */
export interface MightyPane {
  blocks: Readonly<Record<string, MightyBlockCopy>>;
  inputs: MightyInputs | null;
  older: readonly MightyGraphRun[];
  timeline: readonly TimelineGroup[] | null;
  structure: readonly StructureBoard[] | null;
}

export const emptyMighty: MightyPane = { blocks: {}, inputs: null, older: [], timeline: null, structure: null };

export function applyMighty(current: MightyPane | undefined, message: MightyMessage): MightyPane {
  const pane = current ?? emptyMighty;
  let blocks = pane.blocks;
  if (message.blocks || message.removed) {
    const next: Record<string, MightyBlockCopy> = { ...pane.blocks, ...(message.blocks ?? {}) };
    for (const id of message.removed ?? []) delete next[id];
    blocks = next;
  }
  return {
    blocks,
    inputs: message.inputs ?? pane.inputs,
    older: message.older ?? pane.older,
    timeline: message.timeline ?? pane.timeline,
    structure: message.structure ?? pane.structure,
  };
}
