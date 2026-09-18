import type { CommandAction, MessageCommandAction, MobileCommand } from '@/api/types';

/**
 * Slash-command matching for the composer. The host owns the list; the phone only picks
 * what to show while the user is typing and decides who handles the chosen command.
 */

const ACTIONS: readonly CommandAction[] = [
  'model',
  'permission',
  'clear',
  'usage',
  'help',
  'rename',
];

const MESSAGE_ACTIONS: readonly MessageCommandAction[] = ['clear', 'usage', 'help'];

/**
 * The filter the composer is typing, or undefined when the list should not be shown:
 * the text has to start with "/" and stop before the first space, because after that the
 * user is writing arguments.
 */
export function commandQuery(text: string): string | undefined {
  if (!text.startsWith('/')) return undefined;
  const rest = text.slice(1);
  if (/\s/.test(rest)) return undefined;
  return rest.toLowerCase();
}

/** Name first, then description; an empty query lists everything the host offered. */
export function filterCommands(
  commands: readonly MobileCommand[],
  query: string,
): MobileCommand[] {
  const needle = query.toLowerCase();
  if (needle.length === 0) return [...commands];
  const byName = commands.filter((command) => command.name.toLowerCase().includes(needle));
  const rest = commands.filter(
    (command) =>
      !byName.includes(command) && command.description.toLowerCase().includes(needle),
  );
  return [...byName, ...rest];
}

/** An action the phone knows how to run, or undefined for a plain text command. */
export function commandActionOf(command: MobileCommand): CommandAction | undefined {
  return ACTIONS.find((action) => action === command.action);
}

/** True for the three actions that go to `POST /command` instead of a local dialog. */
export function isMessageAction(action: CommandAction): action is MessageCommandAction {
  return MESSAGE_ACTIONS.some((entry) => entry === action);
}

/** What a command without an action puts in the composer. */
export function commandInsertion(command: MobileCommand): string {
  return `/${command.name} `;
}
