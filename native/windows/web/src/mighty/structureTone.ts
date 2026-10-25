// The 파이프라인 view's state colours on the page: the host's tone words (Core MightyStructure.Tone, M/MightyStructureView.swift) as
// classes, and each card's moving edge. The rule itself is the host's; the page only draws it (mighty.css "pipeline").

const tones = new Set(['run', 'wait', 'done', 'err', 'stop']);

/** ` tinted tint-<tone>` for a tone the host sent; nothing for a step still to come or a word this page does not know. */
export function tintClass(tone: string | null | undefined): string {
  return tone && tones.has(tone) ? ' tinted tint-' + tone : '';
}

/** A card's edge while it moves (MightyGraphActivity): the run line while it runs, the wait line while it waits, none once settled. */
export function edgeClass(status: string): string {
  return status === 'running' ? ' live' : status === 'waiting' ? ' waiting' : '';
}
