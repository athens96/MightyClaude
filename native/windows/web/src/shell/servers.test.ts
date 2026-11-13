import { describe, expect, it } from 'vitest';
import { sidebarGroups } from './servers';
import type { Counts, RemoteServer, SidebarWorkspace } from './types';

const counts: Counts = { questions: 0, permissions: 0, errors: 0, running: 0, done: 0, stopped: 0, idle: 0 };
const workspace = (id: string, extra: Partial<SidebarWorkspace> = {}): SidebarWorkspace => ({ id, name: id, path: 'C:\\' + id, local: true, counts, expanded: false, ...extra });
const server = (id: string, extra: Partial<RemoteServer> = {}): RemoteServer => ({ id, title: '원격 · ' + id, line: '100.64.0.2:43137 · 연결됨', state: '연결됨', tone: 'done', connected: true, known: true, help: '', ...extra });

describe('the sidebar server groups', () => {
  const workspaces = [
    workspace('mine'),
    workspace('mirror-a', { local: false, server: 'studio', mirror: true, path: '/Users/x/project' }),
    workspace('old-remote', { local: false, server: 'studio', remoteHost: 'studio-mac' }),
    workspace('mirror-b', { local: false, server: 'laptop', mirror: true }),
    workspace('mine-2'),
  ];

  it("lists this PC's workspaces first and each server's under its header, in the servers' order", () => {
    const groups = sidebarGroups(workspaces, [server('laptop'), server('studio'), server('empty', { connected: false, state: '연결 전', tone: 'idle' })], '');
    expect(groups.local.map((w) => w.id)).toEqual(['mine', 'mine-2']);
    expect(groups.servers.map((g) => [g.server.id, g.workspaces.map((w) => w.id)])).toEqual([['laptop', ['mirror-b']], ['studio', ['mirror-a', 'old-remote']], ['empty', []]]);
    expect(groups.ownCount).toBe(2);
  });

  it('keeps a saved server with no workspace, and drops a server with nothing matching while searching', () => {
    const groups = sidebarGroups(workspaces, [server('studio'), server('empty')], 'project');
    expect(groups.local).toEqual([]);
    expect(groups.servers.map((g) => g.server.id)).toEqual(['studio']);
    expect(groups.servers[0]?.workspaces.map((w) => w.id)).toEqual(['mirror-a']);
    expect(groups.ownCount).toBe(2);
  });

  it('still lists a remote workspace whose server the host did not name', () => {
    const groups = sidebarGroups([workspace('far', { local: false, server: 'gone', remoteHost: 'old-host' })], [], '');
    expect(groups.servers).toHaveLength(1);
    expect(groups.servers[0]?.server).toMatchObject({ id: 'gone', title: 'old-host', known: false });
  });
});
