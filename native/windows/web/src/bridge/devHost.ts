// A stand-in host for `npm run dev` in a plain browser: answers the phase-0 methods and pushes a small fixture state.
import type { AppSnapshot, RunSession } from '../store/models';
import type { HostPort } from './bridge';
import type { CallMessage, HostMessage } from './protocol';

function pane(id: string, workspaceId: string, title: string, status: RunSession['status'], lines: number): RunSession {
  return {
    id, workspaceId, title, status, kind: 'claude', model: 'default', provider: 'claude', createdAt: '2026-10-24T00:00:00Z', draft: '',
    settings: { effort: 'default', permissionMode: 'manual', maxTurns: null, maxBudgetUsd: null },
    logs: Array.from({ length: lines }, (_, index) => ({ id: `${id}-${index}`, kind: index % 2 ? 'assistant' : 'user', text: `Line ${index + 1}`, timestamp: '2026-10-24T00:00:00Z' })),
  };
}

export function devSnapshot(): AppSnapshot {
  return {
    version: 1, layout: 'grid', theme: 'dark', sidebarWidth: 252, sidebarCollapsed: false, statusLineEnabled: true, completionNotificationsEnabled: true,
    claudeDirectUsageLookupEnabled: false, appUpdateAutoCheck: true, languagePreference: 'system', settingsPane: 'general', browserEngineEnabled: false,
    mobileRemote: { enabled: false, relayURL: '', allowLegacyPhones: true, directEnabled: true, directPort: 0 },
    workspaces: [{ id: 'w1', name: 'Fixture project', path: 'fixture', createdAt: '2026-10-24T00:00:00Z' }],
    sessions: [pane('s1', 'w1', 'Claude', 'completed', 80), pane('s2', 'w1', 'Codex', 'running', 3)],
    activeWorkspaceId: 'w1', activeSessionId: 's1',
  };
}

export function devHost(): HostPort {
  const listeners: ((event: { data: unknown }) => void)[] = [];
  let snapshot = devSnapshot();
  const send = (data: HostMessage) => setTimeout(() => listeners.forEach((listener) => listener({ data })), 0);
  return {
    addEventListener: (_type, listener) => void listeners.push(listener),
    postMessage(message) {
      const call = message as CallMessage;
      const args = (call.args ?? {}) as Record<string, string | undefined>;
      if (call.method === 'selectSession') snapshot = { ...snapshot, activeSessionId: args.id };
      if (call.method === 'setTheme') snapshot = { ...snapshot, theme: args.theme ?? snapshot.theme };
      const value = call.method === 'getLocale' ? { preference: 'system', language: navigator.language.split('-')[0] ?? 'en' } : null;
      send({ kind: 'result', id: call.id, ok: true, value });
      if (call.method === 'ready' || call.method === 'selectSession' || call.method === 'setTheme') send({ kind: 'snapshot', snapshot });
    },
  };
}
