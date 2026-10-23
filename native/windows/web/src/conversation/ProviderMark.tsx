import { providerMarkOutline } from '@mobile/lib/provider-marks';

/** The agent's mark (the outlines the Mac, WinUI and phone draw), in the ink of its place: colours come only from the tokens. */
export function ProviderMark({ provider, size }: { provider: string; size: number }) {
  const outline = providerMarkOutline(provider);
  if (!outline) return null;
  return (
    <svg className="provider-mark" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d={outline} fill="currentColor" />
    </svg>
  );
}
