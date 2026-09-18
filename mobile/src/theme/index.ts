import { useMemo } from 'react';
import { Platform, useColorScheme, type TextStyle } from 'react-native';
import type { Provider, SessionStatus, SettingOption } from '@/api/types';

export interface Palette {
  background: string;
  surface: string;
  surfaceRaised: string;
  border: string;
  accent: string;
  accentMuted: string;
  text: string;
  textMuted: string;
  textFaint: string;
  success: string;
  danger: string;
  /** Background behind error banners. */
  dangerSurface: string;
  warning: string;
  grey: string;
  bubbleUser: string;
  /** Text drawn on top of a filled accent/danger surface. */
  onAccent: string;
  /** Text drawn on top of a filled chip or badge. */
  onBadge: string;
  /** Dimming behind modals. */
  overlay: string;
}

export const darkPalette: Palette = {
  background: '#0d0d0f',
  surface: '#17171b',
  surfaceRaised: '#1f1f25',
  border: '#2c2c34',
  accent: '#d97757',
  accentMuted: '#3a231b',
  text: '#ededf0',
  textMuted: '#9a9aa5',
  textFaint: '#6a6a76',
  success: '#5ab87c',
  danger: '#e0625b',
  dangerSurface: '#2a1614',
  warning: '#d9a441',
  grey: '#6a6a76',
  bubbleUser: '#2a2118',
  onAccent: '#1a0f08',
  onBadge: '#14140f',
  overlay: 'rgba(0,0,0,0.6)',
};

export const lightPalette: Palette = {
  background: '#faf9f7',
  surface: '#ffffff',
  surfaceRaised: '#f0eeea',
  border: '#dbd6ce',
  accent: '#b8542f',
  accentMuted: '#f6e1d6',
  text: '#1b1b1f',
  textMuted: '#5c5c66',
  textFaint: '#8a8a94',
  success: '#2c7a4b',
  danger: '#b3352d',
  dangerSurface: '#fae4e1',
  warning: '#9a6b16',
  grey: '#8a8a94',
  bubbleUser: '#f5e7dc',
  onAccent: '#ffffff',
  onBadge: '#ffffff',
  overlay: 'rgba(0,0,0,0.35)',
};

/**
 * The dark palette stays the default: `useColorScheme()` returns null while the system
 * preference is unknown, and only an explicit "light" switches.
 */
export function usePalette(): Palette {
  const scheme = useColorScheme();
  return scheme === 'light' ? lightPalette : darkPalette;
}

const styleCache = new WeakMap<object, Map<Palette, unknown>>();

/**
 * Builds a StyleSheet once per (factory, palette) pair so switching the system theme
 * re-renders with new styles without rebuilding them on every render.
 */
export function useStyles<T>(factory: (palette: Palette) => T): T {
  const palette = usePalette();
  return useMemo(() => {
    let perPalette = styleCache.get(factory as unknown as object);
    if (!perPalette) {
      perPalette = new Map();
      styleCache.set(factory as unknown as object, perPalette);
    }
    const cached = perPalette.get(palette);
    if (cached !== undefined) return cached as T;
    const created = factory(palette);
    perPalette.set(palette, created);
    return created;
  }, [factory, palette]);
}

export const spacing = {
  xs: 4,
  sm: 8,
  md: 12,
  lg: 16,
  xl: 24,
} as const;

export const radius = {
  sm: 6,
  md: 10,
  lg: 14,
} as const;

export const monoFontFamily = Platform.select({
  ios: 'Menlo',
  android: 'monospace',
  default: 'monospace',
});

export const monoText: TextStyle = {
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 18,
};

const statusLabels: Record<SessionStatus, string> = {
  idle: '대기',
  running: '실행 중',
  completed: '완료',
  error: '오류',
  stopped: '중지됨',
};

const kindLabels: Record<string, string> = {
  claude: '에이전트',
  shell: '셸',
};

const providerLabels: Record<Provider, string> = {
  claude: 'Claude',
  codex: 'Codex',
  gemini: 'Gemini',
};

/** Brand marks, as used by the Mac app's provider glyphs. */
export const providerColors: Record<Provider, string[]> = {
  claude: ['#D97757'],
  codex: ['#10A37F'],
  gemini: ['#4285F4', '#9B72CB', '#D96570'],
};

/**
 * Unknown strings from the host must never crash a screen, so every lookup falls back:
 * a value we do not know is shown as-is, and an empty one becomes a neutral label.
 */
function labelFor(table: Record<string, string>, value: string): string {
  return table[value] ?? (value.length > 0 ? value : '알 수 없음');
}

export function statusLabel(status: string): string {
  return labelFor(statusLabels, status);
}

export function statusColor(palette: Palette, status: string): string {
  switch (status) {
    case 'running':
      return palette.accent;
    case 'completed':
      return palette.success;
    case 'error':
      return palette.danger;
    case 'stopped':
      return palette.grey;
    case 'idle':
      return palette.textMuted;
    default:
      return palette.textMuted;
  }
}

export function kindLabel(kind: string): string {
  return labelFor(kindLabels, kind);
}

export function providerLabel(provider: string): string {
  return labelFor(providerLabels, provider);
}

/** Brand colours for a provider, or a neutral single colour for an unknown one. */
export function providerColorsFor(palette: Palette, provider: string): string[] {
  return providerColors[provider as Provider] ?? [palette.textMuted];
}

/**
 * `agentViewMode` is the one setting the host sends without an option list, so the two
 * values the contract fixes are spelled out here.
 */
export const AGENT_VIEW_MODES: SettingOption[] = [
  { id: 'plain', label: '기본' },
  { id: 'mighty', label: 'Mighty' },
];

/** The host's label for an id, falling back to the id itself for values we do not know. */
export function optionLabel(options: readonly SettingOption[], id: string): string {
  const match = options.find((option) => option.id === id);
  if (match && match.label.length > 0) return match.label;
  return id.length > 0 ? id : '알 수 없음';
}
