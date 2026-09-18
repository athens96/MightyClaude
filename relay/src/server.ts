import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import type { Duplex } from 'node:stream';
import { pathToFileURL } from 'node:url';

import { WebSocketServer } from 'ws';

import { loadConfig, type RelayConfig } from './config.ts';
import { RelayHub, type RelayLogger } from './hub.ts';
import { CloseCode, parseSocketParams, WS_PATH } from './protocol.ts';

/** A listening relay instance. */
export interface RelayHandle {
  readonly host: string;
  readonly port: number;
  readonly url: string;
  readonly config: RelayConfig;
  close(): Promise<void>;
}

export interface StartRelayOptions extends Partial<RelayConfig> {
  readonly logger?: RelayLogger;
}

function handleRequest(req: IncomingMessage, res: ServerResponse): void {
  if (req.method === 'GET' && req.url === '/healthz') {
    res.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' });
    res.end('ok');
    return;
  }
  res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' });
  res.end('not found');
}

function rejectUpgrade(socket: Duplex): void {
  socket.write('HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n');
  socket.destroy();
}

/** Creates and starts the relay. */
export async function startRelay(options: StartRelayOptions = {}): Promise<RelayHandle> {
  const { logger, ...overrides } = options;
  const config = loadConfig(overrides);
  const log: RelayLogger = logger ?? ((line) => console.log(line));

  const hub = new RelayHub(config, log);
  const httpServer: Server = createServer(handleRequest);
  const wss = new WebSocketServer({ noServer: true, maxPayload: config.maxPayload });

  httpServer.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    const url = new URL(req.url ?? '/', 'http://relay.invalid');
    if (url.pathname !== WS_PATH) {
      rejectUpgrade(socket);
      return;
    }
    const parsed = parseSocketParams(url.searchParams);
    wss.handleUpgrade(req, socket, head, (ws) => {
      if (!parsed.ok) {
        hub.reject(ws, CloseCode.badRequest, parsed.reason);
        return;
      }
      hub.handleSocket(ws, parsed.params);
    });
  });

  await new Promise<void>((resolve, reject) => {
    httpServer.once('error', reject);
    httpServer.listen(config.port, config.host, () => {
      httpServer.removeListener('error', reject);
      resolve();
    });
  });

  const address = httpServer.address() as AddressInfo;
  const port = address.port;

  return {
    host: config.host,
    port,
    url: `ws://${config.host}:${port}${WS_PATH}`,
    config,
    close: async () => {
      hub.dispose();
      for (const client of wss.clients) client.terminate();
      await new Promise<void>((resolve) => {
        wss.close(() => resolve());
      });
      await new Promise<void>((resolve) => {
        httpServer.close(() => resolve());
      });
    },
  };
}

const entryPath = process.argv[1];
const isEntryPoint = entryPath !== undefined && import.meta.url === pathToFileURL(entryPath).href;

if (isEntryPoint) {
  const handle = await startRelay();
  console.log(`[relay] listening on ${handle.host}:${handle.port} (ws ${WS_PATH}, /healthz)`);
  const shutdown = (signal: string): void => {
    console.log(`[relay] ${signal} received, shutting down`);
    void handle.close().then(() => process.exit(0));
  };
  process.on('SIGINT', () => shutdown('SIGINT'));
  process.on('SIGTERM', () => shutdown('SIGTERM'));
}
