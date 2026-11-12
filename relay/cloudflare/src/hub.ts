/**
 * One serverId's sockets (../src/hub.ts on Cloudflare): the host control socket, client data
 * sockets and their host halves, piped frame for frame. Sockets use the hibernation API, so
 * what must outlive an eviction is on the sockets themselves (tags and attachments); only the
 * frames a client sends before its host half attaches are in memory, and the attach timer
 * that is pending meanwhile keeps the object awake.
 *
 * Differences from the Node relay: the runtime answers pings (the Mac and Windows hosts ping,
 * the phone's encrypted channel pings every 20 s), so the relay sends none; Workers keep no
 * per-socket send buffer to measure, so 4507 never comes; TURN is off (turn-unavailable).
 */

import { DurableObject } from 'cloudflare:workers';

import { CloseCode, sendableCloseCode, type ControlNotice, type TurnUnavailable } from '../../src/protocol.ts';

/** Headers the Worker sets on the request it hands a Hub (never trusted from the client). */
export const HUB_HEADERS = {
  kind: 'x-mighty-relay-kind',
  serverId: 'x-mighty-relay-server-id',
  connectionId: 'x-mighty-relay-connection-id',
  ip: 'x-mighty-relay-ip',
} as const;

type Kind = 'control' | 'client' | 'host';

/** What a socket is, kept on it across hibernation. `done` sockets are being closed by the relay. */
interface Attachment {
  readonly kind: Kind;
  readonly connectionId: string | null;
  readonly serverId: string;
  done?: boolean;
}

interface Pending {
  frames: Array<string | ArrayBuffer>;
  timer: ReturnType<typeof setTimeout>;
}

interface HubEnv {
  readonly RELAY_ATTACH_TIMEOUT_MS?: string;
  readonly RELAY_MAX_CONNECTIONS?: string;
  readonly RELAY_MAX_BUFFERED_FRAMES?: string;
  readonly RELAY_MAX_PAYLOAD?: string;
}

function envInt(raw: string | undefined, fallback: number): number {
  const parsed = Number.parseInt(raw ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

/** Close reasons are capped at 123 bytes by RFC 6455. */
function truncateReason(reason: string): string {
  const encoder = new TextEncoder();
  let out = reason;
  while (encoder.encode(out).byteLength > 123) out = out.slice(0, -1);
  return out;
}

function closeSocket(ws: WebSocket, code: number, reason: string): void {
  try {
    ws.close(sendableCloseCode(code), truncateReason(reason));
  } catch {
    // Already closed or closing.
  }
}

function byteLength(message: string | ArrayBuffer): number {
  return typeof message === 'string' ? new TextEncoder().encode(message).byteLength : message.byteLength;
}

const tagFor = { client: (id: string) => `c:${id}`, host: (id: string) => `h:${id}` } as const;

export class Hub extends DurableObject<HubEnv> {
  readonly #attachTimeoutMs: number;
  readonly #maxConnections: number;
  readonly #maxBufferedFrames: number;
  readonly #maxPayload: number;
  readonly #pending = new Map<string, Pending>();

  constructor(ctx: DurableObjectState, env: HubEnv) {
    super(ctx, env);
    this.#attachTimeoutMs = envInt(env.RELAY_ATTACH_TIMEOUT_MS, 10_000);
    this.#maxConnections = envInt(env.RELAY_MAX_CONNECTIONS, 32);
    this.#maxBufferedFrames = envInt(env.RELAY_MAX_BUFFERED_FRAMES, 64);
    this.#maxPayload = envInt(env.RELAY_MAX_PAYLOAD, 1024 * 1024);
  }

  override async fetch(request: Request): Promise<Response> {
    const kind = request.headers.get(HUB_HEADERS.kind);
    const serverId = request.headers.get(HUB_HEADERS.serverId) ?? '';
    const connectionId = request.headers.get(HUB_HEADERS.connectionId) ?? '';
    const pair = new WebSocketPair();
    const [client, server] = [pair[0], pair[1]];
    switch (kind) {
      case 'control':
        this.#openControl(server, serverId);
        break;
      case 'client-data':
        this.#openClient(server, serverId, connectionId);
        break;
      case 'host-data':
        this.#openHost(server, serverId, connectionId);
        break;
      default:
        server.accept();
        closeSocket(server, CloseCode.badRequest, 'bad role');
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  // --- opening ---------------------------------------------------------------

  #openControl(ws: WebSocket, serverId: string): void {
    for (const previous of this.#live('control')) {
      // The newer control socket inherits the pending and paired connections.
      this.#retire(previous);
      closeSocket(previous, CloseCode.conflict, 'replaced by newer control socket');
    }
    this.#accept(ws, ['control'], { kind: 'control', connectionId: null, serverId });
    this.#log(serverId, 'connect control');
  }

  #openClient(ws: WebSocket, serverId: string, connectionId: string): void {
    if (this.#control() === null) return this.#refuse(ws, CloseCode.notFound, 'no host');
    if (this.#live('client').length >= this.#maxConnections) return this.#refuse(ws, CloseCode.tooManyConnections, 'too many connections');
    if (this.#live(tagFor.client(connectionId)).length > 0) return this.#refuse(ws, CloseCode.badRequest, 'duplicate connectionId');
    this.#accept(ws, ['client', tagFor.client(connectionId)], { kind: 'client', connectionId, serverId });
    this.#pending.set(connectionId, {
      frames: [],
      timer: setTimeout(() => this.#teardown(connectionId, CloseCode.attachTimeout, 'host data socket timeout', null), this.#attachTimeoutMs),
    });
    this.#log(serverId, `connect client ${connectionId}`);
    this.#notify({ type: 'connected', connectionId });
  }

  #openHost(ws: WebSocket, serverId: string, connectionId: string): void {
    if (this.#control() === null || this.#live(tagFor.client(connectionId)).length === 0) {
      return this.#refuse(ws, CloseCode.notFound, 'unknown connectionId');
    }
    if (this.#live(tagFor.host(connectionId)).length > 0) return this.#refuse(ws, CloseCode.notFound, 'connection already attached');
    this.#accept(ws, ['host', tagFor.host(connectionId)], { kind: 'host', connectionId, serverId });
    this.#log(serverId, `connect host-data ${connectionId}`);
    const pending = this.#pending.get(connectionId);
    this.#pending.delete(connectionId);
    if (pending === undefined) return;
    clearTimeout(pending.timer);
    for (const frame of pending.frames) {
      if (this.#done(ws)) break;
      ws.send(frame);
    }
  }

  // --- events ----------------------------------------------------------------

  override webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): void {
    const me = this.#attachment(ws);
    if (me === null || me.done === true) return;
    if (byteLength(message) > this.#maxPayload) {
      if (me.kind === 'control') {
        this.#retire(ws);
        closeSocket(ws, 1009, 'message too big');
        this.#controlGone(me.serverId);
      } else {
        this.#teardown(me.connectionId ?? '', 1009, 'message too big', null);
      }
      return;
    }
    switch (me.kind) {
      case 'control':
        this.#controlMessage(ws, message);
        return;
      case 'client': {
        const id = me.connectionId ?? '';
        const host = this.#live(tagFor.host(id))[0];
        if (host !== undefined) {
          host.send(message);
          return;
        }
        let pending = this.#pending.get(id);
        if (pending === undefined) {
          // The object was evicted between the client's arrival and now: start the wait again.
          pending = { frames: [], timer: setTimeout(() => this.#teardown(id, CloseCode.attachTimeout, 'host data socket timeout', null), this.#attachTimeoutMs) };
          this.#pending.set(id, pending);
        }
        if (pending.frames.length >= this.#maxBufferedFrames) {
          this.#teardown(id, CloseCode.bufferOverflow, 'buffer overflow', null);
          return;
        }
        pending.frames.push(message);
        return;
      }
      case 'host': {
        const client = this.#live(tagFor.client(me.connectionId ?? ''))[0];
        client?.send(message);
        return;
      }
    }
  }

  override webSocketClose(ws: WebSocket, code: number, reason: string): void {
    const me = this.#attachment(ws);
    // A socket the relay closed has finished its handshake; any other is answered here.
    if (me === null || me.done === true) return;
    closeSocket(ws, code, reason);
    this.#retire(ws);
    if (me.kind === 'control') {
      this.#log(me.serverId, 'disconnect control');
      this.#controlGone(me.serverId);
      return;
    }
    this.#teardown(me.connectionId ?? '', code, reason, me.kind);
  }

  override webSocketError(ws: WebSocket): void {
    const me = this.#attachment(ws);
    if (me === null || me.done === true) return;
    this.#retire(ws);
    closeSocket(ws, CloseCode.normal, '');
    if (me.kind === 'control') this.#controlGone(me.serverId);
    else this.#teardown(me.connectionId ?? '', CloseCode.normal, '', me.kind);
  }

  // --- control ---------------------------------------------------------------

  /**
   * Only `turn-credentials-request` is acted on; anything else is ignored (the relay must not
   * act on E2EE frames that reach the control socket by accident). This relay has no TURN.
   */
  #controlMessage(ws: WebSocket, message: string | ArrayBuffer): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(typeof message === 'string' ? message : new TextDecoder().decode(message));
    } catch {
      return;
    }
    if (typeof parsed !== 'object' || parsed === null || (parsed as Record<string, unknown>)['type'] !== 'turn-credentials-request') return;
    const reply: TurnUnavailable = { type: 'turn-unavailable' };
    ws.send(JSON.stringify(reply));
  }

  /** The control socket left without a successor: every connection closes 4410. */
  #controlGone(_serverId: string): void {
    if (this.#control() !== null) return;
    const ids = new Set<string>();
    for (const ws of [...this.#live('client'), ...this.#live('host')]) {
      const id = this.#attachment(ws)?.connectionId;
      if (id) ids.add(id);
    }
    for (const id of this.#pending.keys()) ids.add(id);
    for (const id of ids) this.#teardown(id, CloseCode.hostOffline, 'host offline', null);
  }

  // --- shared ----------------------------------------------------------------

  /** Closes both halves of a connection with the same code and reason and tells the control socket once. */
  #teardown(connectionId: string, code: number, reason: string, initiator: 'client' | 'host' | null): void {
    const pending = this.#pending.get(connectionId);
    if (pending !== undefined) clearTimeout(pending.timer);
    this.#pending.delete(connectionId);
    const halves = [...this.#live(tagFor.client(connectionId)), ...this.#live(tagFor.host(connectionId))];
    if (halves.length === 0 && initiator === null) return;
    let serverId = '';
    for (const ws of halves) {
      const me = this.#attachment(ws);
      serverId = me?.serverId ?? serverId;
      this.#retire(ws);
      if (me?.kind !== initiator) closeSocket(ws, code, reason);
    }
    if (serverId !== '') this.#log(serverId, `disconnect ${connectionId} code=${code}`);
    this.#notify({ type: 'disconnected', connectionId });
  }

  #notify(notice: ControlNotice): void {
    const control = this.#control();
    if (control === null) return;
    try {
      control.send(JSON.stringify(notice));
    } catch {
      // The control socket is going away; its close handler tears down.
    }
  }

  #accept(ws: WebSocket, tags: string[], attachment: Attachment): void {
    this.ctx.acceptWebSocket(ws, tags);
    ws.serializeAttachment(attachment);
  }

  #refuse(ws: WebSocket, code: number, reason: string): void {
    ws.accept();
    closeSocket(ws, code, reason);
  }

  #attachment(ws: WebSocket): Attachment | null {
    const value = ws.deserializeAttachment() as Attachment | null | undefined;
    return value ?? null;
  }

  #done(ws: WebSocket): boolean {
    return this.#attachment(ws)?.done === true;
  }

  /** Marks a socket as one the relay is closing, so its close event changes nothing more. */
  #retire(ws: WebSocket): void {
    const me = this.#attachment(ws);
    if (me !== null) ws.serializeAttachment({ ...me, done: true });
  }

  /** Open sockets under `tag` that the relay is not already closing. */
  #live(tag: string): WebSocket[] {
    return this.ctx.getWebSockets(tag).filter((ws) => ws.readyState === WebSocket.READY_STATE_OPEN && !this.#done(ws));
  }

  #control(): WebSocket | null {
    return this.#live('control')[0] ?? null;
  }

  #log(serverId: string, line: string): void {
    console.log(`[${serverId.slice(0, 8)}] ${line}`);
  }
}
