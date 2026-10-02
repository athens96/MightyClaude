import type { Capability, ScreenGrant, ScreenMode, ScreenRejectReason } from '@/api/types';
import { hasCapability } from '@/lib/capabilities';

/**
 * Who may even see the 화면 보기 entry point. The feature is Android-only in this beta,
 * and a Mac older than it never advertises `screenShare` — in both cases the phone looks
 * exactly as it did before, with no entry point and no screen.
 */
export type ScreenShareHiddenReason = 'platform' | 'capability';

export interface ScreenShareVisibility {
  visible: boolean;
  hiddenBecause?: ScreenShareHiddenReason;
}

export interface ScreenShareAudience {
  /** `Platform.OS`; only `android` carries the feature in this beta. */
  platform: string;
  capabilities: readonly Capability[] | undefined;
}

export function screenShareVisibility({
  platform,
  capabilities,
}: ScreenShareAudience): ScreenShareVisibility {
  // iOS (and web) are out of scope for the beta, so the build stays green with the
  // feature simply absent rather than disabled.
  if (platform !== 'android') return { visible: false, hiddenBecause: 'platform' };
  if (!hasCapability(capabilities, 'screenShare')) {
    return { visible: false, hiddenBecause: 'capability' };
  }
  return { visible: true };
}

/** Convenience for a screen that only wants the boolean. */
export function screenShareAvailable(audience: ScreenShareAudience): boolean {
  return screenShareVisibility(audience).visible;
}

export interface ScreenShareGateInput {
  allowed: boolean;
  grant: ScreenGrant;
  mode: ScreenMode;
  /** Absent on a phone paired before device ids; the Mac cannot tell it apart. */
  clientId?: string;
}

/**
 * What the Mac would say to this request, worked out locally so the phone can grey out a
 * button instead of inviting a refusal. The Mac still decides: this is a mirror of its
 * rules, never a substitute for them.
 */
export function screenShareRefusal({
  allowed,
  grant,
  mode,
  clientId,
}: ScreenShareGateInput): ScreenRejectReason | undefined {
  if (!clientId) return 'legacy-client';
  if (!allowed) return 'device-not-allowed';
  if (grant === 'none') return 'insufficient-grant';
  if (mode === 'control' && grant !== 'control') return 'insufficient-grant';
  return undefined;
}

/** True when this phone may ask for this mode right now. */
export function mayStartScreenShare(input: ScreenShareGateInput): boolean {
  return screenShareRefusal(input) === undefined;
}

/** The modes the current grant offers, widest first. */
export function grantedModes(grant: ScreenGrant): ScreenMode[] {
  if (grant === 'control') return ['control', 'view'];
  if (grant === 'view') return ['view'];
  return [];
}
