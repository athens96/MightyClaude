/** Runtime configuration for the relay. All values are resolved once at startup. */
export interface RelayConfig {
  /** Interface to bind the HTTP server to. */
  readonly host: string;
  /** TCP port to listen on. `0` picks an ephemeral port. */
  readonly port: number;
  /** How long a client data socket waits for its host data socket. */
  readonly attachTimeoutMs: number;
  /** WebSocket ping interval; a socket without a pong within the same span is terminated. */
  readonly pingIntervalMs: number;
  /** Maximum concurrent data connections per serverId. */
  readonly maxConnectionsPerServer: number;
  /** Client frames buffered while the host data socket is still attaching. */
  readonly maxBufferedFrames: number;
  /** Maximum WebSocket frame payload in bytes. */
  readonly maxPayload: number;
}

const DEFAULTS: RelayConfig = {
  host: '0.0.0.0',
  port: 8787,
  attachTimeoutMs: 10_000,
  pingIntervalMs: 30_000,
  maxConnectionsPerServer: 32,
  maxBufferedFrames: 64,
  maxPayload: 1024 * 1024,
};

function envInt(name: string, fallback: number): number {
  const raw = process.env[name];
  if (raw === undefined || raw.trim() === '') return fallback;
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

/** Builds the config from environment variables, with optional programmatic overrides (tests). */
export function loadConfig(overrides: Partial<RelayConfig> = {}): RelayConfig {
  const fromEnv: RelayConfig = {
    host: process.env.HOST?.trim() || DEFAULTS.host,
    port: envInt('PORT', DEFAULTS.port),
    attachTimeoutMs: envInt('RELAY_ATTACH_TIMEOUT_MS', DEFAULTS.attachTimeoutMs),
    pingIntervalMs: envInt('RELAY_PING_INTERVAL_MS', DEFAULTS.pingIntervalMs),
    maxConnectionsPerServer: envInt('RELAY_MAX_CONNECTIONS', DEFAULTS.maxConnectionsPerServer),
    maxBufferedFrames: envInt('RELAY_MAX_BUFFERED_FRAMES', DEFAULTS.maxBufferedFrames),
    maxPayload: envInt('RELAY_MAX_PAYLOAD', DEFAULTS.maxPayload),
  };
  return { ...fromEnv, ...overrides };
}
