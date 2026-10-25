// The 파이프라인 view's state colours on the page: the host's tone words (Core MightyStructure.Tone, M/MightyStructureView.swift) as
// classes, and each card's moving edge. The rule itself is the host's; the page only draws it (mighty.css "pipeline").

const tones = new Set(['run', 'wait', 'done', 'err', 'stop']);

/** ` tinted tint-<tone>` for a tone the host sent; nothing for a step still to come or a word this page does not know. */
export function tintClass(tone: string | null | undefined): string {
  return tone && tones.has(tone) ? ' tinted tint-' + tone : '';
}

/** ` chip-<tone>` for the current step's chip, in its column's tone (amber while its request waits); nothing for any other step. */
export function chipClass(step: string, tone: string | null | undefined): string {
  return step === 'current' && tone && tones.has(tone) ? ' chip-' + tone : '';
}

/** A card's edge while it moves (MightyGraphActivity): the run line while it runs, the wait line while it waits, none once settled. */
export function edgeClass(status: string): string {
  return status === 'running' ? ' live' : status === 'waiting' ? ' waiting' : '';
}
