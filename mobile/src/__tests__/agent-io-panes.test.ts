import { isAgentIOPane, type MobileSessionSummary, type MobileState } from '@/api/types';
import { groupSessionsByWorkspace, mergeState } from '@/lib/merge';
import { kindLabel } from '@/theme';

function session(overrides: Partial<MobileSessionSummary>): MobileSessionSummary {
  return {
    id: 's1',
    workspaceId: 'w1',
    title: '세션',
    kind: 'claude',
    provider: 'claude',
    model: 'opus',
    status: 'idle',
    revision: 1,
    updatedAt: '2026-01-01T00:00:00.000Z',
    pendingPermissions: 0,
    pendingQuestions: 0,
    queued: 0,
    terminal: false,
    ...overrides,
  };
}

/** The pane list the Mac sends for one agent pane that has both IO panes open. */
function stateWithAgentIOPanes(): MobileState {
  return {
    protocol: 1,
    revision: 7,
    hostName: 'mac',
    workspaces: [{ id: 'w1', name: '작업1', path: '/a', remote: false }],
    sessions: [
      session({ id: 'agent-1', title: '내 에이전트' }),
      session({ id: 'shell-1', title: '터미널', kind: 'shell', terminal: true }),
      session({
        id: 'agent-terminal:agent-1',
        title: '내 에이전트 — 터미널',
        kind: 'agent-terminal',
        model: 'default',
        terminal: true,
      }),
      session({
        id: 'agent-browser:agent-1',
        title: '내 에이전트 — 브라우저',
        kind: 'agent-browser',
        model: 'default',
        terminal: true,
      }),
    ],
  };
}

describe('agent-owned IO panes in the pane list', () => {
  it('lists the terminal and browser panes alongside the existing panes', () => {
    const groups = groupSessionsByWorkspace(stateWithAgentIOPanes());
    expect(groups).toHaveLength(1);
    const group = groups[0];
    if (!group) throw new Error('작업 공간 그룹이 없습니다.');
    const ids = group.sessions.map((entry) => entry.id);
    expect(ids).toEqual(
      expect.arrayContaining([
        'agent-1',
        'shell-1',
        'agent-terminal:agent-1',
        'agent-browser:agent-1',
      ]),
    );
    expect(ids).toHaveLength(4);
  });

  it('keeps them through a state merge', () => {
    const merged = mergeState(undefined, stateWithAgentIOPanes());
    expect(merged.sessions.map((entry) => entry.kind)).toEqual(
      expect.arrayContaining(['agent-terminal', 'agent-browser']),
    );
  });

  it('names both kinds in the row meta line', () => {
    expect(kindLabel('agent-terminal')).toBe('에이전트 터미널');
    expect(kindLabel('agent-browser')).toBe('에이전트 브라우저');
  });

  it('marks only the agent-owned panes as agent IO panes', () => {
    expect(isAgentIOPane('agent-terminal')).toBe(true);
    expect(isAgentIOPane('agent-browser')).toBe(true);
    expect(isAgentIOPane('shell')).toBe(false);
    expect(isAgentIOPane('claude')).toBe(false);
  });

  it('carries them as non-commandable, so the phone only lists them', () => {
    const owned = stateWithAgentIOPanes().sessions.filter((entry) => isAgentIOPane(entry.kind));
    expect(owned).toHaveLength(2);
    expect(owned.every((entry) => entry.terminal)).toBe(true);
  });
});
