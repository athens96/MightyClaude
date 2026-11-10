// The shell code blocks of an agent's reply that get a Run button (spec: run-shell-blocks; the host's half is
// MightyCode.Core/ShellBlockRuns.cs and MightyCode.WinUI/MainWindow.ShellBlockRuns.cs). Pure: which blocks, the command
// each runs, and its key, `<entryId>#<n>` with n its place among the entry's runnable blocks.
import type { Block } from './markdown';
import type { ShellBlockRun } from '../store/models';

export type ShellPlatform = 'windows' | 'mac';

const shells = new Set(['sh', 'bash', 'zsh', 'shell', 'console']);
const windowsShells = new Set(['ps1', 'powershell', 'pwsh']);
const prompts: Record<ShellPlatform, readonly string[]> = { windows: ['$ ', 'PS> ', '> '], mac: ['$ ', '% '] };
/**
 * Text that could show one thing and run another (Trojan source): bidi controls, zero-width and format characters, C0 controls
 * but tab and newline (so also ESC and a lone CR), DEL. A block holding any gets no button; the host refuses the same
 * (MightyCode.Core/ShellBlockScan.cs IsDeceptive).
 */
// eslint-disable-next-line no-control-regex
export const deceptiveCharacters = /[\u202A-\u202E\u2066-\u2069\u200B-\u200F\uFEFF\u2060\u0000-\u0008\u000B-\u001F\u007F]/u;

/**
 * The command a fenced block runs, or null when it gets no button: its language's first word (any case) is a shell of this
 * platform's, and something is left to run. A `console` block runs only its prompt lines, the prompt taken off; its other
 * lines are sample output. Any other block runs its text as shown, the trailing newline trimmed.
 */
export function shellCommand(language: string | null | undefined, text: string, platform: ShellPlatform = 'windows'): string | null {
  const name = language?.trim().split(/\s+/)[0]?.toLowerCase();
  if (!name || !(shells.has(name) || (platform === 'windows' && windowsShells.has(name)))) return null;
  // Checked on the block as written, before a lone CR becomes a line break.
  if (deceptiveCharacters.test(text.replace(/\r\n/g, '\n'))) return null;
  const body = text.replace(/\r\n?/g, '\n').replace(/\n+$/, '');
  if (name !== 'console') return body.trim().length > 0 ? body : null;
  const commands: string[] = [];
  for (const line of body.split('\n')) {
    const prompt = prompts[platform].find((mark) => line.startsWith(mark));
    if (prompt !== undefined) commands.push(line.slice(prompt.length));
  }
  const command = commands.join('\n');
  return command.trim().length > 0 ? command : null;
}

export interface RunnableBlock {
  index: number;
  command: string;
}

/**
 * The runnable blocks of one entry's Markdown in reading order (into quotes and list items), each with its place and command.
 * A block whose closing fence has not come yet (still streaming) has none: it would be the last, so the places before it hold.
 */
export function runnableBlocks(blocks: readonly Block[], platform: ShellPlatform = 'windows'): Map<Block, RunnableBlock> {
  const found = new Map<Block, RunnableBlock>();
  const walk = (list: readonly Block[]) => {
    for (const block of list) {
      if (block.type === 'code') {
        const command = block.open ? null : shellCommand(block.language, block.text, platform);
        if (command !== null) found.set(block, { index: found.size, command });
      } else if (block.type === 'quote') walk(block.children);
      else if (block.type === 'list') for (const item of block.items) walk(item.children);
    }
  };
  walk(blocks);
  return found;
}

export function shellBlockKey(entryId: string, index: number): string {
  return entryId + '#' + index;
}

/**
 * Whether another block of the pane is running: one command at a time per pane terminal. With `command`, a run under this key of
 * another command (an earlier text of the entry) is another block's too.
 */
export function shellBlockBusy(runs: readonly ShellBlockRun[] | undefined, key: string, command?: string): boolean {
  return runs?.some((run) => run.state === 'running' && (run.key !== key || (command !== undefined && run.command !== command))) ?? false;
}
