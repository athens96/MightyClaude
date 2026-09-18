import { CAPABILITIES, type Capability, type HostInfo } from '@/api/types';

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
