import type { ScreenCandidateType, ScreenMode, ScreenRejectReason } from '@/api/types';
import { t } from '@/lib/i18n';
import type { ClipboardRefusal } from '@/lib/screen-share/clipboard';
import type { ControlStartFailure } from '@/lib/screen-share/controller';
import type { ScreenShareHiddenReason } from '@/lib/screen-share/availability';
import type { ScreenHold, ScreenStopReason } from '@/lib/screen-share/session';

/**
 * Every sentence the screen-share feature shows, read from the shared locale files. The
 * mapping lives here rather than in the screen so the words a refusal or a kill turns into
 * can be checked in both languages without rendering anything.
 */

export function screenModeLabel(mode: ScreenMode): string {
  return mode === 'control' ? t('phone.screenShare.mode.control') : t('phone.screenShare.mode.view');
}

export function screenStartLabel(mode: ScreenMode): string {
  return mode === 'control'
    ? t('phone.screenShare.start.control')
    : t('phone.screenShare.start.view');
}

/** Why the entry point is not there at all. */
export function screenHiddenText(reason: ScreenShareHiddenReason): string {
  return reason === 'platform'
    ? t('phone.screenShare.hiddenPlatform')
    : t('phone.screenShare.hiddenCapability');
}

/** What the Mac said when it refused the session. */
export function screenRefusalText(reason: ScreenRejectReason): string {
  switch (reason) {
    case 'legacy-client':
      return t('phone.screenShare.refusal.legacyClient');
    case 'device-not-allowed':
      return t('phone.screenShare.refusal.deviceNotAllowed');
    case 'insufficient-grant':
      return t('phone.screenShare.refusal.insufficientGrant');
    case 'control-signature':
      return t('phone.screenShare.refusal.controlSignature');
    case 'concurrency-limit':
      return t('phone.screenShare.refusal.concurrencyLimit');
    case 'screen-permission':
      // The one refusal the user can fix on the Mac, so it gets its own words.
      return t('phone.screenShare.permissionNeeded');
    default:
      return t('phone.screenShare.status.idle');
  }
}

/** The headline for a session the Mac is holding back rather than refusing. */
export function screenHoldTitle(hold: ScreenHold): string {
  switch (hold) {
    case 'screen-permission':
      return t('phone.screenShare.permissionNeeded');
    case 'lock-screen':
      return t('phone.screenShare.hold.lockScreen');
    case 'secure-input':
      return t('phone.screenShare.hold.secureInput');
    default:
      return t('phone.screenShare.status.idle');
  }
}

/** The one hold that comes with something to do about it. */
export function screenHoldDetail(hold: ScreenHold): string | undefined {
  return hold === 'screen-permission' ? t('phone.screenShare.permissionNeededDetail') : undefined;
}

/** Why the session is over, in the Mac's own vocabulary. */
export function screenStoppedText(reason: ScreenStopReason): string {
  switch (reason) {
    case 'user-stop':
      return t('phone.screenShare.stopped.userStop');
    case 'background':
      return t('phone.screenShare.stopped.background');
    case 'peer-failed':
      return t('phone.screenShare.stopped.peerFailed');
    case 'idle-timeout':
      return t('phone.screenShare.stopped.idleTimeout');
    case 'peer-left':
      return t('phone.screenShare.stopped.peerLeft');
    case 'display-gone':
      return t('phone.screenShare.stopped.displayGone');
    case 'revoked':
      return t('phone.screenShare.stopped.revoked');
    case 'grant-downgrade':
      return t('phone.screenShare.stopped.grantDowngrade');
    case 'rekey-pairing':
      return t('phone.screenShare.stopped.rekeyPairing');
    case 'kill-switch':
      return t('phone.screenShare.stopped.killSwitch');
    case 'lock-screen':
      return t('phone.screenShare.hold.lockScreen');
    case 'secure-input':
      return t('phone.screenShare.hold.secureInput');
    case 'concurrency-limit':
      return t('phone.screenShare.refusal.concurrencyLimit');
    default:
      return t('phone.screenShare.status.idle');
  }
}

/** Why this phone could not sign the control challenge. */
export function controlFailureText(failure: ControlStartFailure): string {
  switch (failure) {
    case 'not-enrolled':
      return t('phone.screenShare.control.notEnrolled');
    case 'authentication-failed':
      return t('phone.screenShare.control.authenticationFailed');
    case 'corrupt':
      return t('phone.screenShare.control.corrupt');
    case 'no-challenge':
      return t('phone.screenShare.control.noChallenge');
    default:
      return t('phone.screenShare.refusal.controlSignature');
  }
}

/** Shown by the OS over the fingerprint sheet each time control starts. */
export function controlPromptText(): string {
  return t('phone.screenShare.control.prompt');
}

export function clipboardRefusalText(refusal: ClipboardRefusal): string {
  switch (refusal) {
    case 'empty':
      return t('phone.screenShare.clipboard.empty');
    case 'too-large':
      return t('phone.screenShare.clipboard.tooLarge');
    case 'not-control':
      return t('phone.screenShare.clipboard.notControl');
    case 'concealed':
      return t('phone.screenShare.clipboard.concealed');
    case 'undecodable':
      return t('phone.screenShare.clipboard.undecodable');
    default:
      return t('phone.screenShare.clipboard.undecodable');
  }
}

export function clipboardMovedText(direction: 'to-mac' | 'to-phone'): string {
  return direction === 'to-mac'
    ? t('phone.screenShare.clipboard.movedToMac')
    : t('phone.screenShare.clipboard.movedToPhone');
}

/** Which ICE path carried the video, so the user can tell Wi-Fi from a TURN detour. */
export function candidatePathText(candidateType: ScreenCandidateType): string {
  switch (candidateType) {
    case 'host':
      return t('phone.screenShare.path.host');
    case 'srflx':
      return t('phone.screenShare.path.srflx');
    case 'prflx':
      return t('phone.screenShare.path.prflx');
    case 'relay':
      return t('phone.screenShare.path.relay');
    default:
      return t('phone.screenShare.path.srflx');
  }
}
