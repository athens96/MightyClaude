import { CONNECT_STEPS, needsOnboarding } from '../lib/onboarding';
import { t, resetLanguage } from '../lib/i18n';

describe('CONNECT_STEPS', () => {
  it('has exactly four steps', () => {
    expect(CONNECT_STEPS).toHaveLength(4);
  });

  it('every step has a titleKey and bodyKey', () => {
    for (const step of CONNECT_STEPS) {
      expect(typeof step.titleKey).toBe('string');
      expect(step.titleKey.length).toBeGreaterThan(0);
      expect(typeof step.bodyKey).toBe('string');
      expect(step.bodyKey.length).toBeGreaterThan(0);
    }
  });

  it('step 1 opens Mac Settings / Mobile Remote', () => {
    const step = CONNECT_STEPS[0]!;
    expect(step.titleKey).toBe('phone.connect.step1.title');
    expect(step.bodyKey).toBe('phone.connect.step1.body');
  });

  it('step 4 scans or pastes the link', () => {
    const step = CONNECT_STEPS[3]!;
    expect(step.titleKey).toBe('phone.connect.step4.title');
    expect(step.bodyKey).toBe('phone.connect.step4.body');
  });
});

describe('CONNECT_STEPS locale resolution', () => {
  afterEach(() => resetLanguage());

  it('resolves non-empty English text for every step', () => {
    resetLanguage('en');
    for (const step of CONNECT_STEPS) {
      const title = t(step.titleKey);
      const body = t(step.bodyKey);
      expect(title).not.toBe(step.titleKey);
      expect(title.length).toBeGreaterThan(0);
      expect(body).not.toBe(step.bodyKey);
      expect(body.length).toBeGreaterThan(0);
    }
  });

  it('resolves non-empty Korean text for every step', () => {
    resetLanguage('ko');
    for (const step of CONNECT_STEPS) {
      const title = t(step.titleKey);
      const body = t(step.bodyKey);
      expect(title).not.toBe(step.titleKey);
      expect(title.length).toBeGreaterThan(0);
      expect(body).not.toBe(step.bodyKey);
      expect(body.length).toBeGreaterThan(0);
    }
  });
});

describe('needsOnboarding', () => {
  it('returns true when no hosts are paired', () => {
    expect(needsOnboarding(0)).toBe(true);
  });

  it('returns false when at least one host is paired', () => {
    expect(needsOnboarding(1)).toBe(false);
    expect(needsOnboarding(3)).toBe(false);
  });
});
