import type { ScreenCandidateType, ScreenMode } from '@/api/types';
import { t } from '@/lib/i18n';
import type { ClipboardRefusal } from '@/lib/screen-share/clipboard';
import type { ControlKeyStatus } from '@/lib/screen-share/control-key';
import type {
  ControlKeyEnrolFailure,
  ControlStartFailure,
} from '@/lib/screen-share/controller';
import type { ScreenShareHiddenReason } from '@/lib/screen-share/availability';
import type { MeasurementSummary } from '@/lib/screen-share/measure';
import type { ScreenHold, ScreenRefusal, ScreenStopReason } from '@/lib/screen-share/session';

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

/** What the Mac said when it refused the session, or what went wrong when it said nothing. */
export function screenRefusalText(reason: ScreenRefusal): string {
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
    case 'lock-screen':
      return t('phone.screenShare.hold.lockScreen');
    case 'secure-input':
      return t('phone.screenShare.hold.secureInput');
    case 'session-stopped':
      return t('phone.screenShare.refusal.sessionStopped');
    case 'bad-request':
      return t('phone.screenShare.refusal.badRequest');
    case 'control-key-present':
      return t('phone.screenShare.key.failure.present');
    case 'control-key-pending':
      return t('phone.screenShare.key.failure.pending');
    case 'control-key-not-confirmed':
      return t('phone.screenShare.key.failure.notConfirmed');
    case 'failed':
    default:
      return t('phone.screenShare.refusal.failed');
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
    case 'key-invalidated':
      return t('phone.screenShare.control.keyInvalidated');
    case 'no-authenticator':
      return t('phone.screenShare.control.noAuthenticator');
    case 'unsupported':
      return t('phone.screenShare.control.unsupported');
    case 'keystore':
      return t('phone.screenShare.control.keystore');
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

/** The prompt's way out, where the device PIN is not offered (before Android 11). */
export function controlPromptCancelText(): string {
  return t('phone.screenShare.control.promptCancel');
}

/** Where this phone's control key stands against the Mac's. */
export function controlKeyStatusText(status: ControlKeyStatus): string | undefined {
  switch (status) {
    case 'ready':
      return t('phone.screenShare.key.ready');
    case 'missing':
      return t('phone.screenShare.key.missing');
    case 'host-missing':
      return t('phone.screenShare.key.hostMissing');
    case 'phone-missing':
      return t('phone.screenShare.key.phoneMissing');
    case 'mismatch':
      return t('phone.screenShare.key.mismatch');
    case 'unsupported':
      return t('phone.screenShare.control.unsupported');
    case 'not-needed':
    default:
      return undefined;
  }
}

/** Why the Mac did not take the control key. */
export function controlKeyEnrolFailureText(failure: ControlKeyEnrolFailure): string {
  switch (failure) {
    case 'control-key-present':
      return t('phone.screenShare.key.failure.present');
    case 'control-key-pending':
      return t('phone.screenShare.key.failure.pending');
    case 'control-key-not-confirmed':
      return t('phone.screenShare.key.failure.notConfirmed');
    case 'insufficient-grant':
      return t('phone.screenShare.refusal.insufficientGrant');
    case 'device-not-allowed':
      return t('phone.screenShare.refusal.deviceNotAllowed');
    case 'no-authenticator':
      return t('phone.screenShare.control.noAuthenticator');
    case 'keystore':
      return t('phone.screenShare.control.keystore');
    case 'failed':
    default:
      return t('phone.screenShare.key.failure.failed');
  }
}

export function clipboardRefusalText(refusal: ClipboardRefusal | 'no-reply'): string {
  switch (refusal) {
    case 'not-sent':
      return t('phone.screenShare.clipboard.notSent');
    case 'no-reply':
      return t('phone.screenShare.clipboard.noReply');
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

function figure(value: number | undefined, digits = 0): string {
  if (value === undefined || !Number.isFinite(value)) return t('phone.screenShare.measure.none');
  return value.toFixed(digits);
}

/** The measurement overlay, one line per group of numbers. */
export function measurementLines(summary: MeasurementSummary | undefined): string[] {
  const latency = summary?.latency;
  const bitrate = summary?.bitrate;
  return [
    t('phone.screenShare.measure.network', {
      path: summary?.candidateType
        ? candidatePathText(summary.candidateType)
        : t('phone.screenShare.measure.none'),
      rtt: figure(summary?.rttMs),
      jitter: figure(summary?.jitterMs),
    }),
    t('phone.screenShare.measure.decode', {
      decode: figure(summary?.decodeMsPerFrame, 1),
      freezes: figure(summary?.freezeCount),
      seconds: figure(summary?.freezeSeconds, 1),
    }),
    t('phone.screenShare.measure.latency', {
      p50: figure(latency?.p50Ms),
      p95: figure(latency?.p95Ms),
      count: latency?.count ?? 0,
      timeouts: latency?.timeouts ?? 0,
    }),
    t('phone.screenShare.measure.bitrate', {
      active: figure(bitrate?.activeKbps),
      idle: figure(bitrate?.idleKbps),
    }),
    // Only once the Mac has drawn a marker or refused a marked tap.
    ...(latency?.echoP50Ms !== undefined || latency?.refused
      ? [
          t('phone.screenShare.measure.markerEcho', {
            echo: figure(latency.echoP50Ms),
            refused: latency.refused ?? 0,
          }),
        ]
      : []),
  ];
}

/** How tap-to-visible is timed against this Mac: with its marker, or without one. */
export function measurementMarkerNote(hostTapMarker = false): string {
  return hostTapMarker
    ? t('phone.screenShare.measure.markerOn')
    : t('phone.screenShare.measure.markerMissing');
}
