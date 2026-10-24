// The composer's pills and what their menus and popovers carry, as the host reads them from the WinUI composer
// (MainWindow.WebShell.Composer.cs WebPills, WebMenu, WebRunSettings, WebSessionInfo).

export interface Pills {
  provider: string;
  model: string;
  modelTip: string;
  showsEffort: boolean;
  effort: string;
  effortEnabled: boolean;
  effortTip: string;
  permission: string;
  permissionHelp: string;
  unlocked: boolean;
  showsFast: boolean;
  fast: boolean;
  fastEnabled: boolean;
  moreOn: boolean;
  contextPercent: number | null;
  resuming: boolean;
}

export type MenuRow =
  | { kind: 'separator' }
  | { kind: 'header'; text: string }
  | { kind: 'choice'; text: string; enabled: boolean; checked: boolean; help?: string | null }
  | { kind: 'action'; text: string; enabled: boolean; help?: string | null }
  | { kind: 'runSettings'; text: string }
  | { kind: 'submenu'; text: string; enabled: boolean; items: MenuRow[] };

export type MenuName = 'model' | 'effort' | 'permission' | 'options' | 'actions' | 'style';

export interface RunSettingsValues {
  fastMode: boolean;
  webSearch: string;
  networkAccess: boolean;
  maxTurns: string;
  maxBudgetUsd: string;
}

export interface RunSettingsInfo {
  provider: string;
  beta: boolean;
  model: string;
  modelDescription: string | null;
  modelSource: string;
  permission: string;
  permissionDescription: string;
  approval: string | null;
  fastSupported: boolean;
  webSearch: boolean;
  networkAccess: boolean;
  networkAllowed: boolean;
  networkNote: string;
  maxTurns: boolean;
  maxBudgetUsd: boolean;
  values: RunSettingsValues;
  searchDescriptions: Record<string, string>;
  held: boolean;
}

export interface SessionInfo {
  title: string;
  provider: string;
  providerLabel: string;
  beta: boolean;
  status: string;
  tone: string;
  contextPercent: number | null;
  context: string;
  rows: { key: string; label: string; value: string }[];
  tokenScope: string | null;
  tokens: { key: string; label: string; value: string }[];
  note: string | null;
  cost: { label: string; value: string } | null;
  noUsage: boolean;
  identifiers: { label: string; value: string }[];
  reported: string | null;
  source: string | null;
  received: string | null;
}

/** The settings that a runner without them still holds (the popover's "unsupported" notice, MainWindow.RunSettings.cs). */
export function unsupportedSettings(info: RunSettingsInfo, values: RunSettingsValues): boolean {
  return (values.fastMode && !info.fastSupported) || (values.webSearch !== 'default' && !info.webSearch) || (values.networkAccess && !info.networkAccess);
}

/** The same values with what this runner cannot take put back to its default. */
export function clearUnsupported(info: RunSettingsInfo, values: RunSettingsValues): RunSettingsValues {
  return {
    ...values,
    fastMode: info.fastSupported ? values.fastMode : false,
    webSearch: info.webSearch ? values.webSearch : 'default',
    networkAccess: info.networkAccess ? values.networkAccess : false,
  };
}

/** The ring's word: the percentage, or a dash while the CLI said nothing (ContextUsageRing). */
export function contextLabel(percent: number | null | undefined): string {
  return percent === null || percent === undefined || !Number.isFinite(percent) ? '–' : `${Math.round(Math.min(100, Math.max(0, percent)))}%`;
}
