import { useMemo } from 'react';
import { Platform, useColorScheme, type TextStyle } from 'react-native';
import type { Provider, SessionStatus, SettingOption } from '@/api/types';
import type { Tone } from '@/lib/status-tone';

export interface Palette {
  /** The page under every card. */
  background: string;
  /** Cards, sheets, the composer field and the tab bar. */
  surface: string;
  /** A step inside a card: the last-activity strip, pressed rows, inline code. */
  surfaceRaised: string;
  border: string;
  /** Segmented-control track and empty progress rails. */
  track: string;
  /** Links, the primary button, the selected tab: the "run" blue, kept AA as text too. */
  accent: string;
  /** Soft blue wash behind a selected chip or inline code. */
  accentMuted: string;
  text: string;
  textMuted: string;
  textFaint: string;
  /** Status words drawn on the page or a card (done / error / waiting / stopped). */
  success: string;
  danger: string;
  /** Background behind error banners and the error pill. */
  dangerSurface: string;
  warning: string;
  grey: string;
  /** The user's turn: an ink bubble on the light page, a pale one on the dark page. */
  bubbleUser: string;
  onBubbleUser: string;
  /** Text drawn on top of a filled accent surface (the primary button). */
  onAccent: string;
  /** Text drawn on top of a filled badge (the attention count). */
  onBadge: string;
  /** Dimming behind modals. */
  overlay: string;
  /**
   * Status fills — stat tiles, the conversation hero, timeline nodes and filled pills.
   * Each carries its own text colour: white on run/done/err/stop/idle, dark ink on wait.
   */
  run: string;
  runSoft: string;
  wait: string;
  waitSoft: string;
  onWait: string;
  done: string;
  doneSoft: string;
  err: string;
  stop: string;
  stopSoft: string;
  /** The hero behind a pane that is simply idle. */
  idle: string;
  onStatus: string;
  /** Fenced code: an ink block in both modes. */
  codeSurface: string;
  codeText: string;
  /** Mighty block tints, kept apart by hue; AA as words and under white/ink glyphs. */
  blockAgent: string;
  blockTask: string;
  blockSteer: string;
  blockCompact: string;
  blockQuestion: string;
}

/**
 * "카드 대시보드" (concept D): status first. A cool grey page with white cards by day,
 * deep ink navy with slate cards by night; every state owns a bold fill — run blue, wait
 * amber, done green, error red, stop slate — with a soft tint and a text-safe ink. The
 * concept's hues were darkened where white text needed it — run #2F6BFF → #2A5FEE (and
 * #2459E6 where the blue is a word), error #EF4136 → #D42F22, stop #98A1B3 → #667085 —
 * and wait keeps its amber with dark ink on it. `palette-contrast.test.ts` holds every
 * text pair to WCAG AA (4.5:1) and every status fill to 3:1 as a mark on a card.
 */
export const lightPalette: Palette = {
  background: '#ECEEF3',
  surface: '#FFFFFF',
  surfaceRaised: '#F5F7FB',
  border: '#DEE2EA',
  track: '#DDE1E9',
  accent: '#2459E6',
  accentMuted: '#E6EDFF',
  text: '#0E1320',
  textMuted: '#5A6377',
  textFaint: '#616A7C',
  success: '#06703F',
  danger: '#B42318',
  dangerSurface: '#FDE6E4',
  warning: '#8A5300',
  grey: '#4F5869',
  bubbleUser: '#0E1320',
  onBubbleUser: '#FFFFFF',
  onAccent: '#FFFFFF',
  onBadge: '#2B1B00',
  overlay: 'rgba(14,19,32,0.36)',
  run: '#2A5FEE',
  runSoft: '#E6EDFF',
  wait: '#FFA81F',
  waitSoft: '#FFF3DE',
  onWait: '#2B1B00',
  done: '#08804A',
  doneSoft: '#E2F6EA',
  err: '#D42F22',
  stop: '#667085',
  stopSoft: '#EEF0F4',
  idle: '#0E1320',
  onStatus: '#FFFFFF',
  codeSurface: '#0E1320',
  codeText: '#D8DEEA',
  blockAgent: '#6D3FD9',
  blockTask: '#0A7480',
  blockSteer: '#A33D8F',
  blockCompact: '#4B5BB8',
  blockQuestion: '#8A5300',
};

/**
 * The night side of D: the page is ink navy (never pure black), cards are slate, and the
 * status inks lift to pale tints so they read as words on the dark. Fills stay saturated:
 * the same white-on-colour tiles and hero, the same dark ink on amber.
 */
export const darkPalette: Palette = {
  background: '#0B0F19',
  surface: '#151B29',
  surfaceRaised: '#1D2435',
  border: '#283043',
  track: '#1D2435',
  accent: '#7FA3FF',
  accentMuted: '#1A2750',
  text: '#EEF1F7',
  textMuted: '#A9B1C2',
  textFaint: '#8E97AA',
  success: '#5BD49A',
  danger: '#FF8A80',
  dangerSurface: '#3A1A18',
  warning: '#FFC45C',
  grey: '#A9B1C2',
  bubbleUser: '#EEF1F7',
  onBubbleUser: '#0E1320',
  onAccent: '#0B0F19',
  onBadge: '#2B1B00',
  overlay: 'rgba(0,0,0,0.55)',
  run: '#2A5FEE',
  runSoft: '#1A2750',
  wait: '#FFA81F',
  waitSoft: '#3A2A0D',
  onWait: '#2B1B00',
  done: '#08804A',
  doneSoft: '#0F2E22',
  err: '#D42F22',
  stop: '#667085',
  stopSoft: '#222939',
  idle: '#2A3347',
  onStatus: '#FFFFFF',
  codeSurface: '#05070D',
  codeText: '#D8DEEA',
  blockAgent: '#B9A0FF',
  blockTask: '#5ED3E0',
  blockSteer: '#E58FD0',
  blockCompact: '#9AA6F5',
  blockQuestion: '#FFC45C',
};

/**
 * The light palette is D's own face, but the dark one stays the default while the system
 * preference is unknown: `useColorScheme()` returns null then, and only an explicit
 * "light" switches.
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

/** Moulded, not cut: pills are round, rows and fields 12–14, cards 20, the hero 24. */
export const radius = {
  sm: 6,
  md: 12,
  lg: 16,
  card: 20,
  hero: 24,
  round: 999,
} as const;

/**
 * The heading face: Avenir Next, set bold, for screen titles, stat numbers, card titles
 * and sheet titles. iOS ships it; Android has no Avenir, so it uses the system sans in
 * bold. Hangul falls back to the system Korean face on both.
 */
export const headingFontFamily = Platform.select({
  ios: 'Avenir Next',
  android: 'sans-serif',
  default: '"Avenir Next", Avenir, system-ui, sans-serif',
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
 * The type scale. The heading steps are bold Avenir Next with a little negative tracking;
 * `number` is the big tabular figure on stat tiles, cards and the hero.
 */
export const typeScale = {
  display: { fontFamily: headingFontFamily, fontSize: 30, lineHeight: 36, fontWeight: '700', letterSpacing: -0.5 },
  title: { fontFamily: headingFontFamily, fontSize: 20, lineHeight: 25, fontWeight: '700', letterSpacing: -0.3 },
  heading: { fontFamily: headingFontFamily, fontSize: 16, lineHeight: 21, fontWeight: '700' },
  number: {
    fontFamily: headingFontFamily,
    fontSize: 30,
    lineHeight: 34,
    fontWeight: '700',
    fontVariant: ['tabular-nums'],
  },
  body: { fontSize: 15, lineHeight: 22, fontWeight: '400' },
  caption: { fontSize: 12, lineHeight: 16, fontWeight: '400' },
} as const satisfies Record<string, TextStyle>;

/** A card lifting off the page: a whisper of shadow by day, none needed by night. */
export const cardShadow = {
  shadowColor: '#0F1428',
  shadowOffset: { width: 0, height: 1 },
  shadowOpacity: 0.06,
  shadowRadius: 3,
  elevation: 1,
} as const;

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

/** The fill, its text, the soft tint and the text-safe ink for one status tone. */
export interface ToneColors {
  fill: string;
  onFill: string;
  soft: string;
  ink: string;
}

/**
 * D's colour for a tone (`toneOf` in `@/lib/status-tone`): stat tiles, the hero, filled
 * pills and timeline nodes use `fill`/`onFill`; soft pills and the card edge use
 * `soft`/`ink`. `idle` is neutral ink rather than a state colour.
 */
export function toneColors(palette: Palette, tone: Tone): ToneColors {
  switch (tone) {
    case 'run':
      return { fill: palette.run, onFill: palette.onStatus, soft: palette.runSoft, ink: palette.accent };
    case 'wait':
      return { fill: palette.wait, onFill: palette.onWait, soft: palette.waitSoft, ink: palette.warning };
    case 'done':
      return { fill: palette.done, onFill: palette.onStatus, soft: palette.doneSoft, ink: palette.success };
    case 'err':
      return { fill: palette.err, onFill: palette.onStatus, soft: palette.dangerSurface, ink: palette.danger };
    case 'stop':
      return { fill: palette.stop, onFill: palette.onStatus, soft: palette.stopSoft, ink: palette.grey };
    case 'idle':
      return { fill: palette.idle, onFill: palette.onStatus, soft: palette.stopSoft, ink: palette.textMuted };
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
