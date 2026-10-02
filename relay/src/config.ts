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
  /** Maximum bytes buffered for sending on a single data socket before closing that connection. */
  readonly maxSocketBufferedBytes: number;
  /**
   * coturn use-auth-secret for minting HMAC TURN credentials.
   * Empty string disables TURN credential minting.
   */
  readonly turnSecret: string;
  /** TURN server hostname for the URIs returned to clients. */
  readonly turnHost: string;
  /** TURN server port. */
  readonly turnPort: number;
  /** Credential lifetime in seconds (passed to the client; coturn enforces it). */
  readonly turnCredentialTtlSecs: number;
  /** Sliding window for TURN credential rate limits (ms). */
  readonly turnRateWindowMs: number;
  /** Maximum TURN credential mints per serverId per rate window. */
  readonly turnMaxPerServerId: number;
  /** Maximum TURN credential mints per source IP per rate window. */
  readonly turnMaxPerIp: number;
  /**
   * Maximum TURN credential mints across all hosts per rate window. Host identity is
   * self-certified, so this global budget is what bounds a flood of fresh serverIds.
   */
  readonly turnMaxGlobal: number;
  /**
   * Trust the last `X-Forwarded-For` hop as the client IP (set when the relay sits
   * behind a reverse proxy that appends it, e.g. Caddy). Off by default: the header
   * is client-controlled when nothing in front of the relay rewrites it.
   */
  readonly trustProxy: boolean;
}

const DEFAULTS: RelayConfig = {
  host: '0.0.0.0',
  port: 8787,
  attachTimeoutMs: 10_000,
  pingIntervalMs: 30_000,
  maxConnectionsPerServer: 32,
  maxBufferedFrames: 64,
  maxPayload: 1024 * 1024,
  maxSocketBufferedBytes: 4 * 1024 * 1024,
  turnSecret: '',
  turnHost: '127.0.0.1',
  turnPort: 3478,
  turnCredentialTtlSecs: 3600,
  turnRateWindowMs: 600_000,
  turnMaxPerServerId: 5,
  turnMaxPerIp: 10,
  turnMaxGlobal: 60,
  trustProxy: false,
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
    maxSocketBufferedBytes: envInt('RELAY_MAX_SOCKET_BUFFERED_BYTES', DEFAULTS.maxSocketBufferedBytes),
    turnSecret: process.env['TURN_SECRET']?.trim() ?? DEFAULTS.turnSecret,
    turnHost: process.env['TURN_HOST']?.trim() || DEFAULTS.turnHost,
    turnPort: envInt('TURN_PORT', DEFAULTS.turnPort),
    turnCredentialTtlSecs: envInt('TURN_CREDENTIAL_TTL_SECS', DEFAULTS.turnCredentialTtlSecs),
    turnRateWindowMs: envInt('TURN_RATE_WINDOW_MS', DEFAULTS.turnRateWindowMs),
    turnMaxPerServerId: envInt('TURN_MAX_PER_SERVER_ID', DEFAULTS.turnMaxPerServerId),
    turnMaxPerIp: envInt('TURN_MAX_PER_IP', DEFAULTS.turnMaxPerIp),
    turnMaxGlobal: envInt('TURN_MAX_GLOBAL', DEFAULTS.turnMaxGlobal),
    trustProxy: process.env['RELAY_TRUST_PROXY']?.trim() === '1',
  };
  return { ...fromEnv, ...overrides };
}
