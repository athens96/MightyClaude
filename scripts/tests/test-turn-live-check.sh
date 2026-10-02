#!/bin/bash
# Verifies the live TURN deployment check offline, so the contract the live run
# proves cannot silently drift between runs:
#   - verify-live.sh asserts every required step the beta depends on (healthz,
#     relay-minted credentials, Allocate with them, Allocate without them
#     rejected, relay media ports, OS firewall, security list, VM shape),
#   - the only VM shapes the deploy gates accept are Ampere A1 and the
#     E2.1.Micro the user accepted for the beta,
#   - the 49160-49200 relay media range is the same number in the coturn
#     config, the OS/cloud firewall scripts and the checks,
#   - the coturn config keeps the Micro quotas (2 Mbps per session, 8 Mbps
#     total — coturn counts them in bytes/s, so 250000 and 1000000), REST-only
#     auth, the private/metadata/IPv6/multicast peer denials, no admin CLI and
#     no certificate-less TLS listener,
#   - a broken required step fails the run (no pass-anyway escape hatch).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
D="$ROOT/relay/deploy/oracle"
VERIFY="$D/verify-live.sh"
PREFLIGHT="$D/preflight.sh"
CONF="$D/turnserver.conf"
OPEN_PORT="$D/open-turn-port.sh"
ALLOCATE="$D/turn-allocate-check.py"
MINT="$D/turn-mint-check.mjs"

MIN_PORT=49160
MAX_PORT=49200
ACCEPTED_A1="VM.Standard.A1.Flex"
ACCEPTED_MICRO="VM.Standard.E2.1.Micro"

fail() { echo "FAIL: $1" >&2; exit 1; }
has()  { grep -qF -- "$2" "$1" || fail "$(basename "$1") does not contain: $2"; }

# 0. Every piece of the live check exists and parses.
for f in "$VERIFY" "$PREFLIGHT" "$OPEN_PORT"; do
  [ -s "$f" ] || fail "$(basename "$f") is missing or empty"
  bash -n "$f" || fail "$(basename "$f") is not valid bash"
done
for f in "$CONF" "$ALLOCATE" "$MINT"; do
  [ -s "$f" ] || fail "$(basename "$f") is missing or empty"
done
python3 -m py_compile "$ALLOCATE" || fail "turn-allocate-check.py does not compile"
node --check "$MINT" || fail "turn-mint-check.mjs does not parse"

# 1. verify-live.sh must run every required step, R1 through R9.
for n in 1 2 3 4 5 6 7 8 9; do
  grep -qE "^step R$n " "$VERIFY" || fail "verify-live.sh has no R$n step"
done

# 2. The steps the beta's TURN path depends on must be wired to the real probes.
has "$VERIFY" '/healthz'                       # R1 live relay health
has "$VERIFY" 'turn-mint-check.mjs'            # R2 relay mints for a host socket
has "$VERIFY" 'turn-allocate-check.py --no-credentials'   # R4 no-credential Allocate rejected
has "$VERIFY" '--emit-credential'              # R5 Allocate with minted credentials
has "$VERIFY" 'host-auth-check.mjs'            # R7 forged serverId refused
has "$VERIFY" 'stun-probe.py'                  # R9 reachable from outside

# 3. Relay media range is one number everywhere.
grep -qE "^MIN_PORT=${MIN_PORT}\$" "$VERIFY" || fail "verify-live.sh MIN_PORT is not ${MIN_PORT}"
grep -qE "^MAX_PORT=${MAX_PORT}\$" "$VERIFY" || fail "verify-live.sh MAX_PORT is not ${MAX_PORT}"
grep -qE "^min-port=${MIN_PORT}\$" "$CONF"    || fail "turnserver.conf min-port is not ${MIN_PORT}"
grep -qE "^max-port=${MAX_PORT}\$" "$CONF"    || fail "turnserver.conf max-port is not ${MAX_PORT}"
has "$OPEN_PORT" "udp ${MIN_PORT} ${MAX_PORT}"
has "$VERIFY" "--dport \${MIN_PORT}:\${MAX_PORT} -j ACCEPT"
# R5 pipes the range into the Allocate check so a relayed port outside it fails.
has "$VERIFY" '3478 ${MIN_PORT} ${MAX_PORT}'
has "$ALLOCATE" 'min_port'
has "$ALLOCATE" 'max_port'

# 4. Only the two accepted shapes pass, and the accepted Micro is named.
for f in "$VERIFY" "$PREFLIGHT"; do
  has "$f" "$ACCEPTED_A1"
  has "$f" "$ACCEPTED_MICRO"
done
grep -qF 'fail "pre-flight 실패: ${SHAPE}은 받아들인 shape이 아닙니다' "$PREFLIGHT" \
  || fail "preflight.sh does not stop on an unaccepted shape"
# verify-live.sh's shape case must mark anything else as FAIL.
awk '/^case "\$\{SHAPE\}" in/,/^esac/' "$VERIFY" | grep -qF 'FAIL shape=' \
  || fail "verify-live.sh does not FAIL on an unaccepted shape"

# 5. Live coturn config: the quotas that made Micro acceptable, REST-only auth,
#    and the peer denials. verify-live.sh R8 asserts these against the running
#    config, so the expected values must match this repo's config.
grep -qE '^use-auth-secret$' "$CONF"     || fail "turnserver.conf does not set use-auth-secret"
# coturn reads max-bps and bps-capacity as BYTES per second.
grep -qE '^max-bps=250000$' "$CONF"       || fail "turnserver.conf per-session quota is not 2 Mbps (250000 B/s)"
grep -qE '^bps-capacity=1000000$' "$CONF" || fail "turnserver.conf total quota is not 8 Mbps (1000000 B/s)"
has "$VERIFY" 'check_conf "max-bps=250000"'
has "$VERIFY" 'check_conf "bps-capacity=1000000"'
grep -qE '^no-cli$' "$CONF"               || fail "turnserver.conf leaves the admin CLI on"
grep -qE '^no-multicast-peers$' "$CONF"   || fail "turnserver.conf allows multicast peers"
grep -qE '^tls-listening-port=' "$CONF" && fail "turnserver.conf opens a TLS port without a certificate"
grep -qE '^verbose$' "$CONF" && fail "turnserver.conf logs verbosely"
grep -qE '^no-auth$' "$CONF" && fail "turnserver.conf enables no-auth (public relay)"
for range in \
  '10.0.0.0-10.255.255.255' \
  '172.16.0.0-172.31.255.255' \
  '192.168.0.0-192.168.255.255' \
  '169.254.0.0-169.254.255.255' \
  '169.254.169.254-169.254.169.254' \
  '192.0.2.0-192.0.2.255' \
  '224.0.0.0-239.255.255.255' \
  '::1' \
  'fc00::-fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff' \
  'fe80::-febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff' \
  '::ffff:0.0.0.0-::ffff:255.255.255.255' \
  '64:ff9b::-64:ff9b::ffff:ffff'
do
  grep -qF "denied-peer-ip=$range" "$CONF" || fail "turnserver.conf does not deny peer range $range"
done
for key in max-bps bps-capacity min-port max-port use-auth-secret; do
  grep -qF "$key" <(grep -o 'sudo grep -E "[^"]*"' "$VERIFY") \
    || fail "verify-live.sh R8 does not read $key from the live coturn config"
done

# 6. No pass-anyway escape hatch: a broken required step must exit 1, and the
#    security list is required now that it is open (it used to be advisory).
has "$VERIFY" 'echo "✘ REQUIRED 항목이 깨졌습니다. 위 FAIL 줄을 보세요."'
has "$VERIFY" 'exit 1'
grep -qF 'PENDING' "$VERIFY" && fail "verify-live.sh still has an advisory PENDING step"
# Every FAIL note must raise the failure flag on the same line.
missing="$(grep -n 'note "FAIL' "$VERIFY" | grep -v 'FAILED=1' || true)"
[ -z "$missing" ] || fail "verify-live.sh notes FAIL without setting FAILED=1:
$missing"
notes="$(grep -c 'note "FAIL' "$VERIFY")"
[ "$notes" -ge 8 ] || fail "verify-live.sh has only $notes FAIL notes; expected one per checked fact"

# 7. The credential check must never print the minted password.
has "$MINT" 'password=<'
grep -qF '자' "$MINT" || fail "turn-mint-check.mjs does not report a masked password length"

echo "PASS: live TURN deployment check contract (R1-R9, accepted shapes, ${MIN_PORT}-${MAX_PORT}, quotas)"
