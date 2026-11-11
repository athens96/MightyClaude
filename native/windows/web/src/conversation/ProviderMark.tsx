import { markOutline } from './providerMarks';

/**
 * The agent's mark (the outlines the Mac, WinUI and phone draw; providerMarks.ts). `brand` (the default) draws it in the agent's own
 * colour (windowsOnly.provider in the tokens, M/ProviderMark.swift; pi, opencode and Hermes in conversation.css), as the Mac's sidebar,
 * tabs, header and pills do; otherwise in the ink of its place.
 */
export function ProviderMark({ provider, size, brand = true }: { provider: string; size: number; brand?: boolean }) {
  const outline = markOutline(provider);
  if (!outline) return null;
  return (
    <svg className={'provider-mark' + (brand ? ' brand' : '')} data-provider={provider} width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d={outline} fill="currentColor" />
    </svg>
  );
}
