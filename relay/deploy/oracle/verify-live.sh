#!/usr/bin/env bash
# 용법: bash relay/deploy/oracle/verify-live.sh [--host <도메인>] [--key <ssh 키>]
#
# 살아 있는 Oracle VM의 릴레이·coturn 배포를 확인한다. 이 스크립트가 보장하는
# 범위(REQUIRED)는 "이 저장소가 배포한 것":
#   R1 https://<도메인>/healthz 가 ok
#   R2 wss 제어 소켓에서 turn-credentials 발급(릴레이 TURN 민팅)
#   R3 relay·caddy·coturn 컨테이너가 떠 있음
#   R4 coturn이 인증을 요구함(설정 파일을 실제로 읽었다는 증거)
#   R5 릴레이가 발급한 자격증명으로 Allocate 성공 + 중계 포트가 49160-49200 안
#   R6 VM의 OS 방화벽(iptables)에 3478과 49160-49200이 열려 있음
#   R7 보안 라운드(F-01) 호스트 인증이 반영됨: 위조 serverId는 4401
# 하나라도 깨지면 종료 코드 1.
#
# PENDING 항목은 Oracle 보안 목록(클라우드 방화벽)이라 OCI 로그인이 필요하고,
# 이 스크립트가 바꿀 수 없다. 통과/실패를 그대로 찍지만 종료 코드에는 넣지
# 않는다(아래 요약 참고).
set -uo pipefail

HOST="mightyclaude.duckdns.org"
KEY="${HOME}/.ssh/mightyclaude-relay"
MIN_PORT=49160
MAX_PORT=49200
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

while [ $# -gt 0 ]; do
    case "$1" in
        --host) HOST="$2"; shift 2 ;;
        --key) KEY="$2"; shift 2 ;;
        -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
        *) echo "모르는 인수: $1" >&2; exit 1 ;;
    esac
done

SSH=(ssh -i "${KEY}" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "ubuntu@${HOST}")
REMOTE_DIR="MightyClaude/relay/deploy/oracle"
FAILED=0
PENDING=0

step() { printf '\n[%s] %s\n' "$1" "$2"; }
note() { printf '  %s\n' "$*"; }

echo "▶ 살아 있는 배포 확인: ${HOST}"

# R1 ───────────────────────────────────────────────────────────────────────────
step R1 "릴레이 HTTPS 헬스체크"
BODY="$(curl -fsS --max-time 10 "https://${HOST}/healthz" 2>/dev/null)"
if [ "${BODY}" = "ok" ]; then note "ok   https://${HOST}/healthz → ok"; else note "FAIL https://${HOST}/healthz → '${BODY}'"; FAILED=1; fi

# R2 ───────────────────────────────────────────────────────────────────────────
step R2 "wss 제어 소켓에서 TURN 자격증명 발급"
if node "${SCRIPT_DIR}/turn-mint-check.mjs" "${HOST}"; then :; else FAILED=1; fi

# R3 ───────────────────────────────────────────────────────────────────────────
step R3 "컨테이너 상태"
PS="$("${SSH[@]}" 'sudo docker ps --format "{{.Names}} {{.Status}}"' 2>/dev/null)"
for name in relay caddy coturn; do
    if printf '%s' "${PS}" | grep -q "mightyclaude-relay-${name}-1 Up"; then
        note "ok   ${name}: $(printf '%s' "${PS}" | grep "mightyclaude-relay-${name}-1" | cut -d' ' -f2-)"
    else
        note "FAIL ${name} 컨테이너가 Up이 아닙니다"; FAILED=1
    fi
done

# R4 ───────────────────────────────────────────────────────────────────────────
step R4 "coturn이 인증을 요구하는지(설정 파일을 읽었는지)"
if "${SSH[@]}" "python3 ${REMOTE_DIR}/turn-allocate-check.py --no-credentials \$(hostname -I | awk '{print \$1}') 3478" 2>/dev/null; then :; else
    note "FAIL coturn 인증 확인 실패"; FAILED=1
fi

# R5 ───────────────────────────────────────────────────────────────────────────
step R5 "릴레이가 발급한 자격증명으로 TURN Allocate"
if node "${SCRIPT_DIR}/turn-mint-check.mjs" --emit-credential "${HOST}" 2>/dev/null \
    | "${SSH[@]}" "python3 ${REMOTE_DIR}/turn-allocate-check.py \$(hostname -I | awk '{print \$1}') 3478 ${MIN_PORT} ${MAX_PORT}"; then :; else
    note "FAIL Allocate 실패"; FAILED=1
fi

# R6 ───────────────────────────────────────────────────────────────────────────
step R6 "VM OS 방화벽(iptables)"
RULES="$("${SSH[@]}" 'sudo iptables -S INPUT' 2>/dev/null)"
check_rule() {
    if printf '%s' "${RULES}" | grep -q -- "$1"; then note "ok   $2"; else note "FAIL $2 규칙이 없습니다"; FAILED=1; fi
}
check_rule "-p udp -m udp --dport 3478 -j ACCEPT" "UDP 3478"
check_rule "-p tcp -m tcp --dport 3478 -j ACCEPT" "TCP 3478"
check_rule "-p udp -m udp --dport ${MIN_PORT}:${MAX_PORT} -j ACCEPT" "UDP ${MIN_PORT}-${MAX_PORT} (TURN 중계 포트)"

# R7 ───────────────────────────────────────────────────────────────────────────
step R7 "보안 라운드 호스트 인증(위조 serverId 거절)"
if node "${SCRIPT_DIR}/host-auth-check.mjs" "${HOST}"; then :; else FAILED=1; fi

# PENDING ─────────────────────────────────────────────────────────────────────
step PENDING "밖에서 UDP 3478이 닿는지 (Oracle 보안 목록)"
if python3 "${SCRIPT_DIR}/stun-probe.py" "${HOST}" 3478; then
    note "→ 보안 목록이 열려 있습니다. 강제 TURN 경로를 기기에서 시험할 수 있습니다."
else
    PENDING=1
    cat <<'MSG'
  → Oracle 클라우드 보안 목록(VCN)이 UDP 3478을 막고 있습니다. VM 안의 coturn과
    OS 방화벽은 정상이므로(R4·R5·R6), 남은 것은 보안 목록 한 번 열기뿐입니다.
    OCI 세션은 브라우저 로그인이 필요해 이 자리에서 대신 할 수 없습니다. 사용자가:
      oci session authenticate --profile-name mighty --region ap-singapore-1
      bash relay/deploy/oracle/open-turn-port.sh --profile mighty
    그 뒤 이 스크립트를 다시 돌리면 이 줄이 ok로 바뀝니다.
MSG
fi

# 요약 ────────────────────────────────────────────────────────────────────────
echo ""
if [ "${FAILED}" -ne 0 ]; then
    echo "✘ REQUIRED 항목이 깨졌습니다. 위 FAIL 줄을 보세요."
    exit 1
fi
if [ "${PENDING}" -ne 0 ]; then
    echo "✔ REQUIRED(R1~R7) 전부 통과 — 릴레이·coturn·OS 방화벽은 살아 있습니다."
    echo "⚠ PENDING 1건: Oracle 보안 목록의 UDP 3478·${MIN_PORT}-${MAX_PORT}는 사용자 OCI 로그인이 필요합니다."
    exit 0
fi
echo "✔ 전부 통과 — 릴레이·coturn·방화벽·보안 목록 모두 살아 있습니다."
