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
  /** Mighty block tints: earthy takes on the Mac's SwiftUI system hues, kept apart by hue. */
  blockAgent: string;
  blockTask: string;
  blockSteer: string;
  blockCompact: string;
  blockQuestion: string;
}

/**
 * "Warm paper": the Claude app's look. Ivory paper and ink by day, warm charcoal (never
 * black) by night, and one terracotta accent kept for the primary action, the selected
 * state, links and live indicators. Every text colour clears WCAG AA (4.5:1) on the
 * background and surface it is drawn on; `palette-contrast.test.ts` holds that line.
 */
export const darkPalette: Palette = {
  background: '#262624',
  surface: '#30302e',
  surfaceRaised: '#3a3935',
  border: '#45443f',
  accent: '#dd7e5e',
  accentMuted: '#3e302a',
  text: '#f5f4ee',
  textMuted: '#b5b3aa',
  textFaint: '#a3a198',
  success: '#9cbf72',
  danger: '#e88a74',
  dangerSurface: '#3f2c27',
  warning: '#d6a850',
  grey: '#a3a198',
  bubbleUser: '#393834',
  onAccent: '#1f1e1d',
  onBadge: '#1f1e1d',
  overlay: 'rgba(0,0,0,0.5)',
  blockAgent: '#c79ad6',
  blockTask: '#86b4d9',
  blockSteer: '#ddaa5c',
  blockCompact: '#86c4b0',
  blockQuestion: '#a3a7e6',
};

export const lightPalette: Palette = {
  background: '#f5f4ee',
  surface: '#faf9f5',
  surfaceRaised: '#edebe4',
  border: '#dedad0',
  accent: '#ae4e2b',
  accentMuted: '#f4e4da',
  text: '#1f1e1d',
  textMuted: '#57564f',
  textFaint: '#6c6a63',
  success: '#48722a',
  danger: '#b0392b',
  dangerSurface: '#f5e3dd',
  warning: '#8a6212',
  grey: '#6c6a63',
  bubbleUser: '#eae7de',
  onAccent: '#ffffff',
  onBadge: '#ffffff',
  overlay: 'rgba(31,30,29,0.32)',
  blockAgent: '#8a4e9e',
  blockTask: '#2d6a8c',
  blockSteer: '#8c5b0e',
  blockCompact: '#2b6e5d',
  blockQuestion: '#4f56a3',
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
  xxl: 32,
} as const;

/** Small, quiet corners: paper is cut, not moulded. `round` is for dots and the send button. */
export const radius = {
  sm: 4,
  md: 8,
  lg: 12,
  round: 999,
} as const;

/**
 * The serif for screen titles, large headings, sheet titles and empty states. Georgia is
 * on every iOS device; Android's `serif` is Noto Serif. Hangul has no glyphs in either
 * and falls back to the system Korean face. Body, lists and controls stay system sans.
 */
export const serifFontFamily = Platform.select({
  ios: 'Georgia',
  android: 'serif',
  default: 'Georgia, serif',
});

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

/**
 * The type scale. The serif steps (display, title, heading) carry no weight of their own:
 * the Claude look sets its headings in a regular serif rather than a bold sans.
 */
export const typeScale = {
  display: { fontFamily: serifFontFamily, fontSize: 28, lineHeight: 34, fontWeight: '400' },
  title: { fontFamily: serifFontFamily, fontSize: 22, lineHeight: 28, fontWeight: '400' },
  heading: { fontFamily: serifFontFamily, fontSize: 18, lineHeight: 24, fontWeight: '400' },
  body: { fontSize: 15, lineHeight: 22, fontWeight: '400' },
  caption: { fontSize: 12, lineHeight: 16, fontWeight: '400' },
} as const satisfies Record<string, TextStyle>;

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
  browser: '브라우저',
  'agent-terminal': '에이전트 터미널',
  'agent-browser': '에이전트 브라우저',
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
  // `Object.hasOwn`, because the key is a word the host chose: a plain object answers
  // `constructor` with a function, which reaches `<Text>` as something that is not a
  // string and draws nothing at all.
  const label = Object.hasOwn(table, value) ? table[value] : undefined;
  return label ?? (value.length > 0 ? value : '알 수 없음');
}

/**
 * Everything drawn with a status chip: a pane's own status, plus the `waiting` that
 * `activity.state` and Mighty blocks add and a pane itself never reports.
 */
const chipLabels: Record<string, string> = { ...statusLabels, waiting: '기다리는 중' };

export function statusLabel(status: string): string {
  return labelFor(chipLabels, status);
}

export function statusColor(palette: Palette, status: string): string {
  switch (status) {
    case 'running':
      return palette.accent;
    case 'waiting':
      return palette.warning;
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

/**
 * Claude is official; Codex and Gemini are beta. The phone's one answer for every beta
 * badge — the badge is drawn beside the name and never becomes part of `providerLabel`.
 */
export function providerIsBeta(provider: string): boolean {
  return provider === 'codex' || provider === 'gemini';
}

/**
 * Tint for a Mighty block, mirroring the Mac: the request itself is the accent, and each
 * child kind keeps its own colour. A kind the contract does not list is neutral.
 */
export function blockColor(palette: Palette, kind: string): string {
  switch (kind) {
    case 'main':
      return palette.accent;
    case 'agent':
      return palette.blockAgent;
    case 'task':
      return palette.blockTask;
    case 'steer':
      return palette.blockSteer;
    case 'compact':
      return palette.blockCompact;
    case 'question':
      return palette.blockQuestion;
    default:
      return palette.textMuted;
  }
}

/**
 * A style manifest's palette name (contract 1.10), mapped onto this app's colours. The
 * list is closed on the Mac; a name that arrives anyway draws in the accent, which is
 * also the manifest default, rather than crashing the panel.
 */
export function tintColor(palette: Palette, tint: string | undefined): string {
  switch (tint) {
    case 'purple':
      return palette.blockAgent;
    case 'teal':
      return palette.blockTask;
    case 'indigo':
      return palette.blockQuestion;
    case 'mint':
      return palette.blockCompact;
    case 'orange':
      return palette.blockSteer;
    case 'green':
      return palette.success;
    case 'red':
      return palette.danger;
    case 'secondary':
      return palette.textMuted;
    default:
      return palette.accent;
  }
}

/** Brand colours for a provider, or a neutral single colour for an unknown one. */
export function providerColorsFor(palette: Palette, provider: string): string[] {
  const colors = Object.hasOwn(providerColors, provider)
    ? providerColors[provider as Provider]
    : undefined;
  return colors ?? [palette.textMuted];
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
