import type { RawData, WebSocket } from 'ws';

import type { RelayConfig } from './config.ts';
import {
  CloseCode,
  sendableCloseCode,
  truncateReason,
  type ControlNotice,
  type SocketParams,
} from './protocol.ts';

/** A frame held while the host data socket is still attaching. */
interface BufferedFrame {
  readonly payload: Buffer;
  readonly binary: boolean;
}

interface Connection {
  readonly serverId: string;
  readonly connectionId: string;
  readonly client: WebSocket;
  host: WebSocket | null;
  buffered: BufferedFrame[];
  attachTimer: NodeJS.Timeout | null;
  closed: boolean;
}

interface ServerEntry {
  control: WebSocket | null;
  readonly connections: Map<string, Connection>;
}

/** Sink for one-line connect/disconnect logs. */
export type RelayLogger = (line: string) => void;

function toBuffer(data: RawData): Buffer {
  if (Buffer.isBuffer(data)) return data;
  if (Array.isArray(data)) return Buffer.concat(data);
  return Buffer.from(data);
}

function shortId(serverId: string): string {
  return serverId.slice(0, 8);
}

/**
 * Stateless-per-process pairing engine: it matches a host control socket with client
 * data sockets and their host counterparts, then pipes frames verbatim.
 */
export class RelayHub {
  readonly #config: RelayConfig;
  readonly #log: RelayLogger;
  readonly #servers = new Map<string, ServerEntry>();
  readonly #alive = new Map<WebSocket, boolean>();
  #keepalive: NodeJS.Timeout | null = null;

  constructor(config: RelayConfig, log: RelayLogger = (line) => console.log(line)) {
    this.#config = config;
    this.#log = log;
    this.#startKeepalive();
  }

  /** Entry point for an accepted WebSocket with an already validated query. */
  handleSocket(ws: WebSocket, params: SocketParams): void {
    this.#track(ws);
    switch (params.kind) {
      case 'control':
        this.#handleControl(ws, params.serverId);
        return;
      case 'client-data':
        this.#handleClientData(ws, params.serverId, params.connectionId ?? '');
        return;
      case 'host-data':
        this.#handleHostData(ws, params.serverId, params.connectionId ?? '');
        return;
    }
  }

  /** Rejects a socket that failed query validation. */
  reject(ws: WebSocket, code: number, reason: string): void {
    closeSocket(ws, code, reason);
  }

  /** Stops the keepalive timer; call when the HTTP server shuts down. */
  dispose(): void {
    if (this.#keepalive !== null) {
      clearInterval(this.#keepalive);
      this.#keepalive = null;
    }
    this.#alive.clear();
  }

  // --- control socket ------------------------------------------------------

  #handleControl(ws: WebSocket, serverId: string): void {
    let entry = this.#servers.get(serverId);
    if (entry === undefined) {
      entry = { control: null, connections: new Map() };
      this.#servers.set(serverId, entry);
    }

    const previous = entry.control;
    entry.control = ws;
    if (previous !== null && previous !== ws) {
      // The newer control socket inherits the pending/paired connections.
      closeSocket(previous, CloseCode.conflict, 'replaced by newer control socket');
    }
    this.#log(`[${shortId(serverId)}] connect control`);

    ws.on('close', () => {
      this.#untrack(ws);
      this.#log(`[${shortId(serverId)}] disconnect control`);
      const current = this.#servers.get(serverId);
      if (current === undefined || current.control !== ws) return;
      current.control = null;
      for (const connection of [...current.connections.values()]) {
        this.#teardown(connection, CloseCode.hostOffline, 'host offline', null);
      }
      if (current.connections.size === 0) this.#servers.delete(serverId);
    });
    ws.on('error', () => closeSocket(ws, CloseCode.normal, ''));
  }

  // --- client data socket --------------------------------------------------

  #handleClientData(ws: WebSocket, serverId: string, connectionId: string): void {
    const entry = this.#servers.get(serverId);
    if (entry === undefined || entry.control === null) {
      this.#untrack(ws);
      closeSocket(ws, CloseCode.notFound, 'no host');
      return;
    }
    if (entry.connections.size >= this.#config.maxConnectionsPerServer) {
      this.#untrack(ws);
      closeSocket(ws, CloseCode.tooManyConnections, 'too many connections');
      return;
    }
    if (entry.connections.has(connectionId)) {
      this.#untrack(ws);
      closeSocket(ws, CloseCode.badRequest, 'duplicate connectionId');
      return;
    }

    const connection: Connection = {
      serverId,
      connectionId,
      client: ws,
      host: null,
      buffered: [],
      attachTimer: null,
      closed: false,
    };
    entry.connections.set(connectionId, connection);
    connection.attachTimer = setTimeout(() => {
      this.#teardown(connection, CloseCode.attachTimeout, 'host data socket timeout', null);
    }, this.#config.attachTimeoutMs);
    connection.attachTimer.unref?.();

    this.#log(`[${shortId(serverId)}] connect client ${connectionId}`);
    this.#notify(entry, { type: 'connected', connectionId });

    ws.on('message', (data: RawData, isBinary: boolean) => {
      const payload = toBuffer(data);
      if (connection.host !== null) {
        forward(connection.host, payload, isBinary);
        return;
      }
      if (connection.buffered.length >= this.#config.maxBufferedFrames) {
        this.#teardown(connection, CloseCode.bufferOverflow, 'buffer overflow', null);
        return;
      }
      connection.buffered.push({ payload, binary: isBinary });
    });
    ws.on('close', (code: number, reason: Buffer) => {
      this.#untrack(ws);
      this.#teardown(connection, code, reason.toString('utf8'), 'client');
    });
    ws.on('error', () => closeSocket(ws, CloseCode.normal, ''));
  }

  // --- host data socket ----------------------------------------------------

  #handleHostData(ws: WebSocket, serverId: string, connectionId: string): void {
    const entry = this.#servers.get(serverId);
    const connection = entry?.connections.get(connectionId);
    if (entry === undefined || entry.control === null || connection === undefined) {
      this.#untrack(ws);
      closeSocket(ws, CloseCode.notFound, 'unknown connectionId');
      return;
    }
    if (connection.host !== null || connection.closed) {
      this.#untrack(ws);
      closeSocket(ws, CloseCode.notFound, 'connection already attached');
      return;
    }

    connection.host = ws;
    if (connection.attachTimer !== null) {
      clearTimeout(connection.attachTimer);
      connection.attachTimer = null;
    }
    this.#log(`[${shortId(serverId)}] connect host-data ${connectionId}`);

    const pending = connection.buffered;
    connection.buffered = [];
    for (const frame of pending) forward(ws, frame.payload, frame.binary);

    ws.on('message', (data: RawData, isBinary: boolean) => {
      forward(connection.client, toBuffer(data), isBinary);
    });
    ws.on('close', (code: number, reason: Buffer) => {
      this.#untrack(ws);
      this.#teardown(connection, code, reason.toString('utf8'), 'host');
    });
    ws.on('error', () => closeSocket(ws, CloseCode.normal, ''));
  }

  // --- shared --------------------------------------------------------------

  /**
   * Closes both halves of a connection with the same code/reason, drops it from the
   * server entry, and notifies the control socket exactly once.
   */
  #teardown(
    connection: Connection,
    code: number,
    reason: string,
    initiator: 'client' | 'host' | null,
  ): void {
    if (connection.closed) return;
    connection.closed = true;

    if (connection.attachTimer !== null) {
      clearTimeout(connection.attachTimer);
      connection.attachTimer = null;
    }
    connection.buffered = [];

    const entry = this.#servers.get(connection.serverId);
    if (entry?.connections.get(connection.connectionId) === connection) {
      entry.connections.delete(connection.connectionId);
      if (entry.control === null && entry.connections.size === 0) {
        this.#servers.delete(connection.serverId);
      }
    }

    if (initiator !== 'client') closeSocket(connection.client, code, reason);
    if (initiator !== 'host' && connection.host !== null) {
      closeSocket(connection.host, code, reason);
    }

    this.#log(
      `[${shortId(connection.serverId)}] disconnect ${connection.connectionId} code=${code}`,
    );
    if (entry !== undefined) {
      this.#notify(entry, { type: 'disconnected', connectionId: connection.connectionId });
    }
  }

  #notify(entry: ServerEntry, notice: ControlNotice): void {
    const control = entry.control;
    if (control === null || control.readyState !== control.OPEN) return;
    control.send(JSON.stringify(notice));
  }

  #track(ws: WebSocket): void {
    this.#alive.set(ws, true);
    ws.on('pong', () => {
      if (this.#alive.has(ws)) this.#alive.set(ws, true);
    });
    ws.on('close', () => this.#untrack(ws));
  }

  #untrack(ws: WebSocket): void {
    this.#alive.delete(ws);
  }

  #startKeepalive(): void {
    this.#keepalive = setInterval(() => {
      for (const [ws, alive] of this.#alive) {
        if (!alive) {
          this.#alive.delete(ws);
          ws.terminate();
          continue;
        }
        this.#alive.set(ws, false);
        if (ws.readyState === ws.OPEN) ws.ping();
      }
    }, this.#config.pingIntervalMs);
    this.#keepalive.unref?.();
  }
}

function forward(target: WebSocket, payload: Buffer, binary: boolean): void {
  if (target.readyState !== target.OPEN) return;
  target.send(payload, { binary });
}

function closeSocket(ws: WebSocket, code: number, reason: string): void {
  if (ws.readyState === ws.CLOSED || ws.readyState === ws.CLOSING) return;
  ws.close(sendableCloseCode(code), truncateReason(reason));
}
