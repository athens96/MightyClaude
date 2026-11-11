import { CAPABILITIES, type Capability, type HostInfo, type Provider } from '@/api/types';

const KNOWN = new Set<string>(CAPABILITIES);

/**
 * Reads `/m1/info`'s `capabilities`. Hosts older than the extension send nothing, and
 * unknown names are dropped rather than trusted, so a feature appears only when this
 * exact host says it supports it.
 */
export function parseCapabilities(info: Pick<HostInfo, 'capabilities'> | undefined): Capability[] {
  const raw = info?.capabilities;
  if (!Array.isArray(raw)) return [];
  const seen = new Set<Capability>();
  for (const entry of raw) {
    if (typeof entry === 'string' && KNOWN.has(entry)) seen.add(entry as Capability);
  }
  return [...seen];
}

/** True only when the host advertised the capability. */
export function hasCapability(
  capabilities: readonly Capability[] | undefined,
  name: Capability,
): boolean {
  return capabilities !== undefined && capabilities.includes(name);
}

/** The agents every host with the `pane` route runs, in the desktops' order. */
const BASE_PROVIDERS: readonly Provider[] = ['claude', 'codex', 'gemini'];
/** The later agents, each offered only by a host that lists its name as a capability. */
const LISTED_PROVIDERS: readonly (Provider & Capability)[] = ['pi', 'opencode', 'hermes'];

/**
 * The agents a new pane may run on this host: the three every host runs, then pi, opencode and Hermes where the
 * host lists them (the desktops' `ProviderOptions.ids` order). These three run on the host's own computer only
 * (`ProviderOptions.isLocalOnly`); a host lists only its own workspaces to a phone, so no workspace offered here
 * is another computer's.
 */
export function newPaneProviders(capabilities: readonly Capability[] | undefined): Provider[] {
  return [...BASE_PROVIDERS, ...LISTED_PROVIDERS.filter((name) => hasCapability(capabilities, name))];
}
