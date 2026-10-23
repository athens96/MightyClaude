// The slash palette's rules, mirrored from C/SlashCommands.cs (SlashCommandCatalog.Query/ArgumentQuery/Filter, SlashPalette.
// Draft/Rows/State, SlashPaletteState). The rows themselves are the host's (`slashCommands`: built-ins, scanned skills and
// commands, each argument's choices), so only the filtering, ranking and keys run here, on every keystroke.

export type SlashAction = 'OpenPlugins' | 'NewConversation' | 'ShowUsage' | 'OpenSettings' | 'Rename' | 'Help' | 'SetModel' | 'SetPermission';

export interface SlashCommand {
  invocation: string;
  description: string;
  source: string;
  origin: string;
  action?: SlashAction | null;
  argument?: 'Model' | 'Permission' | null;
  actionArg?: string | null;
}

/** What `slashCommands` answers for a pane. `choices` are each built-in's argument rows, by its invocation. */
export interface SlashCatalog {
  builtins: SlashCommand[];
  scanned: SlashCommand[];
  choices: Record<string, SlashCommand[]>;
}

export const maximumRows = 60;
const namePattern = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;
const whitespace = /\s/;

/** "/ar" → "ar", "/" → ""; null once whitespace follows or the draft is not a command. */
export function query(draft: string): string | null {
  if (!draft.startsWith('/')) return null;
  const rest = draft.slice(1);
  if (whitespace.test(rest) || rest.length > 80) return null;
  return rest;
}

/** "/model cla" → model + "cla"; null unless one space follows a plain command name. */
export function argumentQuery(draft: string): { command: string; query: string } | null {
  if (!draft.startsWith('/') || draft.length > 160) return null;
  const rest = draft.slice(1);
  const space = rest.indexOf(' ');
  if (space < 0) return null;
  const command = rest.slice(0, space);
  const value = rest.slice(space + 1);
  if (whitespace.test(value) || !namePattern.test(command)) return null;
  return { command, query: value };
}

/** Prefix matches, then a prefix after a colon, then a substring of the name or description. */
export function filter(commands: readonly SlashCommand[], text: string): SlashCommand[] {
  const needle = text.toLowerCase();
  if (needle.length === 0) return [...commands];
  const prefix = commands.filter((c) => c.invocation.toLowerCase().startsWith(needle));
  const afterColon = commands.filter((c) => !prefix.includes(c) && c.invocation.split(':').slice(1).some((part) => part.toLowerCase().startsWith(needle)));
  const contains = commands.filter((c) => !prefix.includes(c) && !afterColon.includes(c) && (c.invocation.toLowerCase().includes(needle) || c.description.toLowerCase().includes(needle)));
  return [...prefix, ...afterColon, ...contains];
}

/** The draft the palette is open for, or null (never on a shell pane, or for the draft Esc closed it on). */
export function paletteDraft(kind: string, draft: string, dismissedFor: string | null): string | null {
  if (kind === 'shell' || dismissedFor === draft) return null;
  return query(draft) !== null || argumentQuery(draft) !== null ? draft : null;
}

/** The rows for a draft: built-ins and scanned commands (a built-in's name wins), or a built-in's argument choices. */
export function rows(catalog: SlashCatalog, kind: string, draft: string): SlashCommand[] {
  if (kind === 'shell') return [];
  const typed = query(draft);
  if (typed !== null) {
    const names = new Set(catalog.builtins.map((c) => c.invocation));
    return filter([...catalog.builtins, ...catalog.scanned.filter((c) => !names.has(c.invocation))], typed);
  }
  const parsed = argumentQuery(draft);
  if (!parsed) return [];
  const builtin = catalog.builtins.find((c) => c.invocation === parsed.command);
  if (!builtin?.argument) return [];
  return filter(catalog.choices[builtin.invocation] ?? [], parsed.command + ' ' + parsed.query);
}

export interface PaletteState {
  commands: SlashCommand[];
  index: number;
}

export const closed: PaletteState = { commands: [], index: 0 };

export function safeIndex(state: PaletteState): number {
  return state.commands.length === 0 ? 0 : Math.min(Math.max(state.index, 0), state.commands.length - 1);
}

/** The palette for a draft; the highlight stays where it was when the rows are the same, else goes to the top. */
export function paletteState(catalog: SlashCatalog, kind: string, draft: string, dismissedFor: string | null, previous: PaletteState = closed): PaletteState {
  const text = paletteDraft(kind, draft, dismissedFor);
  if (text === null) return closed;
  const found = rows(catalog, kind, text).slice(0, maximumRows);
  if (found.length === 0) return closed;
  const same = found.length === previous.commands.length && found.every((c, i) => c.invocation === previous.commands[i]?.invocation);
  return { commands: found, index: same ? safeIndex(previous) : 0 };
}

/** Up and Down wrap. */
export function move(state: PaletteState, by: 1 | -1): PaletteState {
  const count = state.commands.length;
  return count === 0 ? state : { ...state, index: (safeIndex(state) + by + count) % count };
}

export type SlashChoice =
  | { effect: 'insert' | 'argument'; draft: string }
  | { effect: 'action'; draft: ''; action: SlashAction; actionArg: string | null };

/** An app action clears the draft; a built-in with an argument leaves "/name " open on its choices; else "/name " is inserted. */
export function choose(command: SlashCommand): SlashChoice {
  if (command.action) return { effect: 'action', draft: '', action: command.action, actionArg: command.actionArg ?? null };
  return { effect: command.argument ? 'argument' : 'insert', draft: '/' + command.invocation + ' ' };
}

export type PaletteKey = 'up' | 'down' | 'choose' | 'dismiss' | null;

/** What a key does while the palette is open: Up/Down move, Enter/Tab choose (not with Shift), Esc dismisses. */
export function paletteKey(key: string, shift: boolean): PaletteKey {
  switch (key) {
    case 'ArrowUp': return 'up';
    case 'ArrowDown': return 'down';
    case 'Enter': case 'Tab': return shift ? null : 'choose';
    case 'Escape': return 'dismiss';
    default: return null;
  }
}
