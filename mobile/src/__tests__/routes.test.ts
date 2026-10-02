import fs from 'node:fs';
import path from 'node:path';
import { getRoutes } from 'expo-router/build/getRoutes';
import type { RouteNode } from 'expo-router/build/Route';

/**
 * The route tree the files under `app/` make. Concept D put a tab bar at the root; the
 * links the app and the Mac hand out — pairing, the connection guide, a host, a pane,
 * a workspace's files — must keep their paths through that.
 */
const APP = path.join(__dirname, '..', '..', 'app');

function routeFiles(directory: string, prefix = '.'): string[] {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const relative = `${prefix}/${entry.name}`;
    if (entry.isDirectory()) return routeFiles(path.join(directory, entry.name), relative);
    return /\.tsx?$/.test(entry.name) ? [relative] : [];
  });
}

function context() {
  const files = routeFiles(APP);
  const load = (() => ({ default: () => null })) as unknown as {
    (key: string): unknown;
    keys: () => string[];
    resolve: (key: string) => string;
    id: string;
  };
  load.keys = () => files;
  load.resolve = (key) => key;
  load.id = 'app';
  return load;
}

/** `route` paths joined from the root, groups dropped, the way a URL reads them. */
function urls(node: RouteNode, parent = ''): string[] {
  const own = node.route
    .split('/')
    .filter((part) => part.length > 0 && !/^\(.*\)$/.test(part) && part !== 'index' && part !== '_layout')
    .join('/');
  const here = [parent, own].filter((part) => part.length > 0).join('/');
  if (node.children.length === 0) return [`/${here}`];
  return node.children.flatMap((child) => urls(child, here));
}

const root = getRoutes(context() as never, {
  internal_stripLoadRoute: true,
  platform: 'ios',
  skipGenerated: true,
} as never);

it('builds a route tree from the app directory', () => {
  expect(root).not.toBeNull();
});

it('keeps every link the app and the Mac hand out', () => {
  const paths = urls(root as RouteNode);
  expect(paths).toEqual(
    expect.arrayContaining([
      '/',
      '/pair',
      '/connect',
      '/host/[hostId]',
      '/host/[hostId]/screen',
      '/host/[hostId]/session/[sessionId]',
      '/host/[hostId]/workspace/[workspaceId]/files',
      '/host/[hostId]/workspace/[workspaceId]/file',
    ]),
  );
});

it('puts the four tabs under one group at the root, with 현황 on "/"', () => {
  const tabs = (root as RouteNode).children.find((child) => child.route === '(tabs)');
  expect(tabs?.children.map((child) => child.route).sort()).toEqual(['alerts', 'hosts', 'index', 'sessions']);
  expect(urls(tabs as RouteNode)).toContain('/');
});

it('keeps a pane and its files above the tabs, not inside them', () => {
  const top = (root as RouteNode).children.map((child) => child.route);
  expect(top).toEqual(
    expect.arrayContaining([
      'host/[hostId]/screen',
      'host/[hostId]/session/[sessionId]',
      'host/[hostId]/workspace/[workspaceId]/files',
      'host/[hostId]/workspace/[workspaceId]/file',
    ]),
  );
  // Only one screen answers "/": the old host list moved to the 호스트 tab.
  expect(urls(root as RouteNode).filter((url) => url === '/')).toHaveLength(1);
});
