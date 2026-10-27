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
}

/** One tool call on a card: its kind (command, read, edit, search, web, agent, turn, tool), its state and its words. */
export interface StructureActivity { kind: string; state: string; text: string }

export interface StructureColumn { id: string; title: string; step: string; stepWord: string; count: number; cards: StructureCard[]; /** The step's state colour (MightyStructure.Tone.OfColumn), none while it is to come. */ tone?: string | null }
export interface StructureBoard { id: string; kind: string; ordinal: number; headline?: string; status: string; statusWord: string; tone: string; runIds: string[]; columns: StructureColumn[]; styleName?: string }

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
