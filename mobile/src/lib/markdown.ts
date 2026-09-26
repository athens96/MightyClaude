/**
 * Text as the phone hands it to the markdown renderer. Results reach the phone
 * trimmed to a character budget, so a code fence can arrive opened and never
 * closed — which would swallow everything after it into one grey box. GitHub task
 * list items have no renderer plugin here, so they become ballot boxes the plain
 * list rule draws as they are.
 */
export function prepareMarkdown(text: string): string {
  const normalized = text.replace(/\r\n?/g, '\n');
  const tasks = normalized.replace(/^(\s*(?:[-*+]|\d+[.)])\s+)\[( |x|X)\]\s/gm, (_match, lead: string, mark: string) =>
    `${lead}${mark === ' ' ? '☐' : '☑'} `,
  );
  return closeOpenFence(tasks);
}

/** Closes a trailing ``` or ~~~ fence the cut left open. */
function closeOpenFence(text: string): string {
  let open: string | null = null;
  for (const line of text.split('\n')) {
    const fence = /^\s{0,3}(`{3,}|~{3,})/.exec(line)?.[1];
    if (fence === undefined) continue;
    if (open === null) open = fence;
    else if (fence[0] === open[0] && fence.length >= open.length && line.trim() === fence) open = null;
  }
  if (open === null) return text;
  return `${text}${text.endsWith('\n') ? '' : '\n'}${open}`;
}

/** Only these open outside the app; anything else in a result is left as text. */
export function isOpenableLink(url: string): boolean {
  return /^(https?:|mailto:)/i.test(url.trim());
}
