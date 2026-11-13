// The sidebar's server groups (M/AppStore+RemoteMirror.swift remoteServerGroups, docs/remote-workspaces.md "그 컴퓨터의 창 그대로"):
// this PC's workspaces first, then each server's header with its remote workspaces under it. A saved connection keeps its header
// with no workspace; while searching, a server with nothing matching steps aside.
import { filterWorkspaces } from './filter';
import type { RemoteServer, SidebarWorkspace } from './types';

export interface ServerGroup {
  server: RemoteServer;
  workspaces: SidebarWorkspace[];
}

export interface SidebarGroups {
  local: SidebarWorkspace[];
  servers: ServerGroup[];
  /** The header's "워크스페이스 N": this PC's own only. */
  ownCount: number;
}

export function sidebarGroups(workspaces: readonly SidebarWorkspace[], servers: readonly RemoteServer[] | undefined, search: string): SidebarGroups {
  const listed = filterWorkspaces(workspaces, search);
  const searching = search.trim().length > 0;
  const local = listed.filter((w) => w.local);
  const known = new Set((servers ?? []).map((s) => s.id));
  const groups: ServerGroup[] = (servers ?? []).map((server) => ({ server, workspaces: listed.filter((w) => !w.local && w.server === server.id) }));
  // A remote workspace whose server the host did not name (it should) still shows, under a header of its own.
  for (const workspace of listed.filter((w) => !w.local && (!w.server || !known.has(w.server)))) {
    const id = workspace.server ?? 'remote-' + workspace.id;
    const group = groups.find((g) => g.server.id === id);
    if (group) group.workspaces.push(workspace);
    else groups.push({ server: { id, title: workspace.remoteHost ?? workspace.name, line: '', state: '', tone: 'idle', connected: false, known: false, help: workspace.path }, workspaces: [workspace] });
  }
  return { local, servers: searching ? groups.filter((g) => g.workspaces.length > 0) : groups, ownCount: workspaces.filter((w) => w.local).length };
}
