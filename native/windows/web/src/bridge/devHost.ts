// A stand-in host for `npm run dev` in a plain browser: answers the page's methods, pushes a small fixture state, then
// changes as deltas made by the host's own rules (store/delta.ts), with the running pane streaming its last reply.
import { diffSession, type SessionDelta } from '../store/delta';
import type { AppSnapshot, LogEntry, RunSession } from '../store/models';
import type { HostPort } from './bridge';
import type { CallMessage, HostMessage } from './protocol';

const at = '2026-10-24T00:00:00Z';

function pane(id: string, workspaceId: string, title: string, status: RunSession['status'], lines: number): RunSession {
  return {
    id, workspaceId, title, status, kind: 'claude', model: 'default', provider: 'claude', createdAt: at, draft: '',
    settings: { effort: 'default', permissionMode: 'manual', maxTurns: null, maxBudgetUsd: null },
    logs: Array.from({ length: lines }, (_, index) => ({ id: `${id}-${index}`, kind: index % 2 ? 'assistant' : 'user', text: `Line ${index + 1}`, timestamp: at })),
  };
}

/** One of each kind the conversation draws (the web smoke's fixture holds the same, MainWindow.WebShell.cs). */
export function fixtureEntries(prefix: string): LogEntry[] {
  return [
    { id: prefix + '-u', kind: 'user', text: 'Summarize the build and fix README.md:12', timestamp: at },
    { id: prefix + '-t1', kind: 'assistant', text: '', timestamp: at, activity: { id: 'a1', provider: 'claude', kind: 'command', state: 'completed', summary: 'npm run build', toolName: 'Bash', output: 'built in 1.2s', durationMs: 1234 } },
    { id: prefix + '-t2', kind: 'assistant', text: '', timestamp: at, activity: { id: 'a2', provider: 'claude', kind: 'read', state: 'error', summary: 'src/missing.ts', toolName: 'Read' } },
    {
      id: prefix + '-a', kind: 'assistant', timestamp: at,
      text: '## Result\n\nThe build **passed**; see `src/app/App.tsx:3` and [the docs](https://example.com).\n\n| file | lines |\n|:--|--:|\n| a.ts | 12 |\n| b.ts | 40 |\n\n- [x] build\n- [ ] deploy\n\n> quoted *note*\n\n```ts\nconst answer = 42;\n```',
    },
    { id: prefix + '-o', kind: 'output', text: 'PS> dir\n  a.txt\n  b.txt', timestamp: at },
    { id: prefix + '-s', kind: 'system', text: 'Model changed to default', timestamp: at },
    { id: prefix + '-e', kind: 'error', text: 'Request failed: network', timestamp: at },
  ];
}

export function devSnapshot(): AppSnapshot {
  return {
    version: 1, layout: 'grid', theme: 'dark', sidebarWidth: 252, sidebarCollapsed: false, statusLineEnabled: true, completionNotificationsEnabled: true,
    claudeDirectUsageLookupEnabled: false, appUpdateAutoCheck: true, languagePreference: 'system', settingsPane: 'general', browserEngineEnabled: false,
    mobileRemote: { enabled: false, relayURL: '', allowLegacyPhones: true, directEnabled: true, directPort: 0 },
    workspaces: [{ id: 'w1', name: 'Fixture project', path: 'fixture', createdAt: at }],
    sessions: [pane('s1', 'w1', 'Claude', 'completed', 80), pane('s2', 'w1', 'Codex', 'running', 3)],
    activeWorkspaceId: 'w1', activeSessionId: 's1',
  };
}

export function devHost(): HostPort {
  const listeners: ((event: { data: unknown }) => void)[] = [];
  const first = devSnapshot();
  let snapshot: AppSnapshot = { ...first, sessions: [{ ...first.sessions[0]!, logs: [...first.sessions[0]!.logs, ...fixtureEntries('f')] }, first.sessions[1]!] };
  let sent = snapshot;
  let streaming = false;
  const send = (data: HostMessage) => setTimeout(() => listeners.forEach((listener) => listener({ data })), 0);
  const flush = () => {
    const before = new Map(sent.sessions.map((session) => [session.id, session]));
    const sessions = snapshot.sessions.map((session) => diffSession(before.get(session.id), session)).filter((delta): delta is SessionDelta => delta !== null);
    const { sessions: _all, ...state } = snapshot;
    sent = snapshot;
    send({ kind: 'delta', sessions, removed: [], state });
  };
  const stream = () => {
    snapshot = {
      ...snapshot,
      sessions: snapshot.sessions.map((session) => {
        if (session.status !== 'running') return session;
        const last = session.logs[session.logs.length - 1]!;
        const grown: LogEntry = last.kind === 'assistant' && last.text.length < 600 ? { ...last, text: last.text + ' more words' } : { id: `${session.id}-${session.logs.length}`, kind: 'assistant', text: 'Streaming', timestamp: at };
        return { ...session, logs: grown.id === last.id ? [...session.logs.slice(0, -1), grown] : [...session.logs, grown] };
      }),
    };
    flush();
  };
  return {
    addEventListener: (_type, listener) => void listeners.push(listener),
    postMessage(message) {
      const call = message as CallMessage;
      const args = (call.args ?? {}) as Record<string, string | undefined>;
      if (call.method === 'selectSession') snapshot = { ...snapshot, activeSessionId: args.id };
      if (call.method === 'setTheme') snapshot = { ...snapshot, theme: args.theme ?? snapshot.theme };
      const value = call.method === 'getLocale' ? { preference: 'system', language: navigator.language.split('-')[0] ?? 'en' } : null;
      send({ kind: 'result', id: call.id, ok: true, value });
      if (call.method === 'ready') {
        sent = snapshot;
        send({ kind: 'snapshot', snapshot });
        if (!streaming) {
          streaming = true;
          setInterval(stream, 250);
        }
      }
      if (call.method === 'selectSession' || call.method === 'setTheme') flush();
    },
  };
}
