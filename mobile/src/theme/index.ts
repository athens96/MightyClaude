import { Platform, type TextStyle } from 'react-native';
import type { SessionStatus } from '@/api/types';

export const colors = {
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
  warning: '#d9a441',
  grey: '#6a6a76',
  bubbleUser: '#2a2118',
} as const;

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

export const statusColors: Record<SessionStatus, string> = {
  idle: colors.textMuted,
  running: colors.accent,
  completed: colors.success,
  error: colors.danger,
  stopped: colors.grey,
};

export const statusLabels: Record<SessionStatus, string> = {
  idle: '대기',
  running: '실행 중',
  completed: '완료',
  error: '오류',
  stopped: '중지됨',
};

export const providerLabels = {
  claude: 'Claude',
  codex: 'Codex',
  gemini: 'Gemini',
} as const;

export const kindLabels = {
  claude: '에이전트',
  shell: '셸',
} as const;
