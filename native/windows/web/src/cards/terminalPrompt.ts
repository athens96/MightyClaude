// The terminal prompt card's pure parts (docs/terminal-prompts.md).

const markers = ['❯', '›', '>', '●', '◉', '◆', '→'];

/** Whether a choice's line is the one marked in the terminal (TerminalPromptDetector.isMarked). */
export function isMarked(option: string): boolean {
  const start = option.trim();
  return markers.some((marker) => start.startsWith(marker));
}

/** Which yes/no button carries "(default)" and which is highlighted: yes when the terminal has no default. */
export function yesNoEmphasis(defaultYes?: boolean): { yesDefault: boolean; noDefault: boolean; yesAccent: boolean; noAccent: boolean } {
  return { yesDefault: defaultYes === true, noDefault: defaultYes === false, yesAccent: defaultYes !== false, noAccent: defaultYes === false };
}
