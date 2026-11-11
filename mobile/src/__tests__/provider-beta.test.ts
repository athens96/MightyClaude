import { resetLanguage, t } from '@/lib/i18n';
import { darkPalette, providerColorsFor, providerIsBeta, providerLabel } from '@/theme';

describe('beta providers', () => {
  afterEach(() => {
    resetLanguage();
  });

  it('names pi "pi" whatever the host runs for gemini', () => {
    expect(providerLabel('pi')).toBe('pi');
    expect(providerLabel('pi', false)).toBe('pi');
  });

  it('names opencode and Hermes as the desktops do', () => {
    expect(providerLabel('opencode')).toBe('opencode');
    expect(providerLabel('hermes', false)).toBe('Hermes');
  });

  it("gives opencode and Hermes the desktops' brand colours, and an unknown agent the neutral one", () => {
    // The Mac's ProviderMark.colors and Windows' ProviderMark.Colors.
    expect(providerColorsFor(darkPalette, 'opencode')).toEqual(['#8A8F98']);
    expect(providerColorsFor(darkPalette, 'hermes')).toEqual(['#D9A441']);
    for (const unknown of ['rovo', '', 'constructor', '__proto__']) expect(providerColorsFor(darkPalette, unknown)).toEqual([darkPalette.textMuted]);
    expect(providerLabel('constructor')).toBe('constructor');
  });

  it('marks every agent but Claude as beta', () => {
    expect(providerIsBeta('claude')).toBe(false);
    expect(providerIsBeta('codex')).toBe(true);
    expect(providerIsBeta('gemini')).toBe(true);
    expect(providerIsBeta('pi')).toBe(true);
    expect(providerIsBeta('opencode')).toBe(true);
    expect(providerIsBeta('hermes')).toBe(true);
    expect(providerIsBeta('')).toBe(false);
    expect(providerIsBeta('constructor')).toBe(false);
  });

  it('never changes the provider name itself', () => {
    expect(['claude', 'codex', 'gemini'].map((provider) => providerLabel(provider))).toEqual(['Claude', 'Codex', 'Antigravity']);
    // A host still on Gemini CLI names its `gemini` panes Gemini; the others keep their names.
    expect(['claude', 'codex', 'gemini'].map((provider) => providerLabel(provider, false))).toEqual(['Claude', 'Codex', 'Gemini']);
  });

  it('reads the badge text from the shared locale key in both languages', () => {
    resetLanguage('ko');
    expect(t('badge.beta')).toBe('베타');
    expect(t('badge.betaAccessibility')).toBe('베타 기능');
    resetLanguage('en');
    expect(t('badge.beta')).toBe('Beta');
    expect(t('badge.betaAccessibility')).toBe('Beta feature');
  });
});
