/**
 * The Mighty Code relay on Cloudflare Workers. Same wire as the Node relay (../src/server.ts,
 * docs/relay.md): `GET /healthz`, `GET /ws?...` and `/push/v1/*`. Sockets go to the Hub of
 * their serverId (hub.ts), pushes to the one PushStore (push.ts).
 */

import { CloseCode, parseSocketParams, WS_PATH } from '../../src/protocol.ts';
import { ownsServerIdAsync } from './identity.ts';
import { HUB_HEADERS } from './hub.ts';

export { Hub } from './hub.ts';
export { PushStore } from './push.ts';

export interface Env {
  readonly HUB: DurableObjectNamespace;
  readonly PUSH: DurableObjectNamespace;
  /** The FCM service account JSON (a secret); absent or broken leaves sending off (503). */
  readonly FCM_SERVICE_ACCOUNT?: string;
}

function text(status: number, body: string): Response {
  return new Response(body, { status, headers: { 'content-type': 'text/plain; charset=utf-8' } });
}

/** Accepts the upgrade only to close it at once with `code`, as the Node relay does for a bad query. */
function refuseSocket(code: number, reason: string): Response {
  const pair = new WebSocketPair();
  const [client, server] = [pair[0], pair[1]];
  server.accept();
  server.close(code, reason);
  return new Response(null, { status: 101, webSocket: client });
}

/** The address per-IP limits count: Cloudflare's view of the client. */
export function clientIp(request: Request): string {
  return request.headers.get('cf-connecting-ip') ?? '0.0.0.0';
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const upgrade = request.headers.get('upgrade')?.toLowerCase() === 'websocket';

    if (upgrade) {
      if (url.pathname !== WS_PATH) return text(404, 'not found');
      const parsed = parseSocketParams(url.searchParams);
      if (!parsed.ok) return refuseSocket(CloseCode.badRequest, parsed.reason);
      const { params } = parsed;
      if (params.kind !== 'client-data' && !(await ownsServerIdAsync(params.serverId, params.hostToken))) {
        return refuseSocket(CloseCode.unauthorized, 'invalid or missing host token');
      }
      const headers = new Headers(request.headers);
      headers.set(HUB_HEADERS.kind, params.kind);
      headers.set(HUB_HEADERS.serverId, params.serverId);
      headers.set(HUB_HEADERS.connectionId, params.connectionId ?? '');
      headers.set(HUB_HEADERS.ip, clientIp(request));
      const hub = env.HUB.get(env.HUB.idFromName(params.serverId));
      return hub.fetch(new Request(request.url, { method: 'GET', headers }));
    }

    if (url.pathname.startsWith('/push/v1/')) {
      const headers = new Headers(request.headers);
      headers.set(HUB_HEADERS.ip, clientIp(request));
      const store = env.PUSH.get(env.PUSH.idFromName('push'));
      const init: RequestInit = { method: request.method, headers };
      if (request.method !== 'GET' && request.method !== 'HEAD') init.body = await request.arrayBuffer();
      return store.fetch(new Request(request.url, init));
    }

    if (request.method === 'GET' && url.pathname === '/healthz' && url.search === '') return text(200, 'ok');
    return text(404, 'not found');
  },
} satisfies ExportedHandler<Env>;
