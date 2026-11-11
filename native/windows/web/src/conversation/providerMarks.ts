// The agents' marks the page draws: the phone's outlines (mobile/src/lib/provider-marks.ts, the Mac's ProviderMark.swift) and the
// two the phone does not carry yet, copied from MightyCode.Core/ProviderMark.cs (its Outline): opencode's drawn block O and Hermes's
// drawn winged H. Each sits in the 24×24 box and uses only absolute move / line / close commands.
import { providerMarkOutline } from '@mobile/lib/provider-marks';

/**
 * opencode's O is a frame whose lower half is solid: its hole is wound the other way round from the frame, so the path's nonzero
 * fill (the SVG default, WinUI's FillRule.Nonzero) leaves it open. Hermes's H and its two wings do not touch.
 */
export const pageMarkOutlines = {
  opencode: 'M3 3 L21 3 L21 21 L3 21 Z M7 7 L7 13 L17 13 L17 7 Z',
  hermes: 'M7 6 L10 6 L10 10.5 L14 10.5 L14 6 L17 6 L17 21 L14 21 L14 13.5 L10 13.5 L10 21 L7 21 Z M1 3 L6 5.5 L6 9.5 L3.5 9.5 Z M23 3 L18 5.5 L18 9.5 L20.5 9.5 Z',
} as const;

/** The outline for a provider, or `undefined` for a provider with no mark (ProviderMark.MarkedProvider). */
export function markOutline(provider: string): string | undefined {
  if (provider === 'opencode' || provider === 'hermes') return pageMarkOutlines[provider];
  return providerMarkOutline(provider);
}
