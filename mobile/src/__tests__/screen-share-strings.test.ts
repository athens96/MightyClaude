import { SCREEN_KILL_REASONS, SCREEN_REJECT_REASONS } from '@/api/types';
import { resetLanguage, t } from '@/lib/i18n';
import {
  candidatePathText,
  clipboardMovedText,
  clipboardRefusalText,
  controlFailureText,
  controlKeyEnrolFailureText,
  controlKeyStatusText,
  controlPromptCancelText,
  controlPromptText,
  screenHiddenText,
  screenHoldDetail,
  screenHoldTitle,
  screenModeLabel,
  screenRefusalText,
  screenStartLabel,
  screenStoppedText,
} from '@/lib/screen-share/strings';

/**
 * Every refusal, kill and hold has words in both languages, read from the shared locale
 * files rather than written into the screen. The one the user is told to act on —
 * "Mac에서 승인 필요" — is checked by hand, because it is the sentence the device checklist
 * looks for.
 */

afterEach(() => {
  resetLanguage();
});

describe('the Screen Recording hold', () => {
  it('says "Mac에서 승인 필요" in Korean, and the same thing in English', () => {
    resetLanguage('ko');
    expect(screenHoldTitle('screen-permission')).toBe('Mac에서 승인 필요');
    expect(screenRefusalText('screen-permission')).toBe('Mac에서 승인 필요');
    expect(screenHoldDetail('screen-permission')).toContain('화면 기록');
    resetLanguage('en');
    expect(screenHoldTitle('screen-permission')).toBe('Approval needed on the Mac');
    expect(screenHoldDetail('screen-permission')).toContain('Screen Recording');
  });
});

describe('the lock screen and secure input', () => {
  it('say the picture stopped, in both languages, and carry no extra instruction', () => {
    resetLanguage('ko');
    expect(screenHoldTitle('lock-screen')).toBe('Mac이 잠겨 있어 화면이 멈췄습니다.');
    expect(screenHoldTitle('secure-input')).toContain('암호');
    expect(screenHoldDetail('lock-screen')).toBeUndefined();
    resetLanguage('en');
    expect(screenHoldTitle('lock-screen')).toContain('locked');
    expect(screenHoldTitle('secure-input')).toContain('password');
  });
});

describe('every reason has words', () => {
  it('for each refusal the contract lists, in both languages', () => {
    for (const language of ['ko', 'en'] as const) {
      resetLanguage(language);
      for (const reason of SCREEN_REJECT_REASONS) {
        const text = screenRefusalText(reason);
        expect(text.length).toBeGreaterThan(0);
        // A missing key would come back as the key itself.
        expect(text).not.toContain('phone.screenShare');
      }
    }
  });

  it('for each kill reason, and for each way a session ends on its own', () => {
    for (const language of ['ko', 'en'] as const) {
      resetLanguage(language);
      for (const reason of [
        ...SCREEN_KILL_REASONS,
        'user-stop',
        'background',
        'peer-failed',
        'idle-timeout',
        'peer-left',
        'display-gone',
      ] as const) {
        expect(screenStoppedText(reason)).not.toContain('phone.screenShare');
      }
    }
  });

  it('for each reason the phone could not sign a control challenge', () => {
    for (const language of ['ko', 'en'] as const) {
      resetLanguage(language);
      for (const failure of [
        'not-enrolled',
        'authentication-failed',
        'key-invalidated',
        'no-authenticator',
        'unsupported',
        'keystore',
        'no-challenge',
      ] as const) {
        expect(controlFailureText(failure)).not.toContain('phone.screenShare');
      }
    }
  });

  it('for each clipboard refusal, and for a transfer that worked', () => {
    for (const language of ['ko', 'en'] as const) {
      resetLanguage(language);
      for (const refusal of [
        'empty',
        'too-large',
        'not-control',
        'concealed',
        'undecodable',
        'not-sent',
        'no-reply',
      ] as const) {
        expect(clipboardRefusalText(refusal)).not.toContain('phone.screenShare');
      }
      expect(clipboardMovedText('to-mac')).not.toContain('phone.screenShare');
      expect(clipboardMovedText('to-phone')).not.toContain('phone.screenShare');
    }
  });

  it('for a start that failed without a reason from the Mac', () => {
    resetLanguage('ko');
    expect(screenRefusalText('failed')).toContain('시작하지 못했습니다');
    resetLanguage('en');
    expect(screenRefusalText('failed')).toContain('Could not start');
  });

  it('for where the control key stands, and why the Mac did not take it', () => {
    for (const language of ['ko', 'en'] as const) {
      resetLanguage(language);
      for (const status of ['ready', 'missing', 'host-missing', 'phone-missing', 'mismatch', 'unsupported'] as const) {
        expect(controlKeyStatusText(status)).toBeDefined();
        expect(controlKeyStatusText(status)).not.toContain('phone.screenShare');
      }
      expect(controlKeyStatusText('not-needed')).toBeUndefined();
      for (const failure of [
        'control-key-present',
        'control-key-pending',
        'control-key-not-confirmed',
        'insufficient-grant',
        'device-not-allowed',
        'no-authenticator',
        'keystore',
        'failed',
      ] as const) {
        expect(controlKeyEnrolFailureText(failure)).not.toContain('phone.screenShare');
      }
      expect(controlPromptCancelText()).not.toContain('phone.screenShare');
    }
  });

  it('for each ICE path, so a TURN detour reads differently from the same Wi-Fi', () => {
    resetLanguage('ko');
    expect(candidatePathText('host')).toBe('같은 와이파이');
    expect(candidatePathText('relay')).toBe('TURN 경유');
    expect(candidatePathText('srflx')).toBe(candidatePathText('prflx'));
    resetLanguage('en');
    expect(candidatePathText('host')).toBe('Same Wi-Fi');
    expect(candidatePathText('relay')).toBe('Through TURN');
  });
});

describe('the words on the buttons', () => {
  it('name the two modes and what starting each one does', () => {
    resetLanguage('ko');
    expect(screenModeLabel('view')).toBe('보기');
    expect(screenModeLabel('control')).toBe('조작');
    expect(screenStartLabel('view')).toBe('화면 보기 시작');
    expect(screenStartLabel('control')).toBe('조작 시작');
    resetLanguage('en');
    expect(screenModeLabel('control')).toBe('Control');
    expect(screenStartLabel('view')).toBe('Start viewing');
  });

  it('explain an absent entry point when the screen is opened by link anyway', () => {
    resetLanguage('ko');
    expect(screenHiddenText('platform')).toContain('안드로이드');
    expect(screenHiddenText('capability')).toContain('Mac');
    resetLanguage('en');
    expect(screenHiddenText('platform')).toContain('Android');
  });

  it('give the OS something to put over the fingerprint sheet', () => {
    resetLanguage('ko');
    expect(controlPromptText()).toBe('조작을 시작하려면 본인 확인이 필요합니다.');
    resetLanguage('en');
    expect(controlPromptText()).toContain('Confirm');
  });

  it('carry the BETA badge from the shared key, not from a word of their own', () => {
    resetLanguage('ko');
    expect(t('badge.beta')).toBe('베타');
    resetLanguage('en');
    expect(t('badge.beta')).toBe('Beta');
  });
});
