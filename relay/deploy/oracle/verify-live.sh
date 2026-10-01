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
#   R8 VM shape이 베타에서 받아들인 shape(E2.1.Micro 또는 A1)이고, 살아 있는
#      coturn 설정에 Micro를 받아들인 근거인 할당량(세션당 2 Mbps·합계 8 Mbps)과
#      중계 포트 범위가 그대로 들어 있음
#   R9 밖에서 UDP 3478이 닿음(Oracle 보안 목록이 열려 있음)
# 하나라도 깨지면 종료 코드 1.
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

# R8 ───────────────────────────────────────────────────────────────────────────
step R8 "VM shape과 coturn 할당량"
SHAPE="$("${SSH[@]}" 'curl -fsS -H "Authorization: Bearer Oracle" --max-time 10 http://169.254.169.254/opc/v2/instance/' 2>/dev/null \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("shape") or "?")' 2>/dev/null)"
case "${SHAPE}" in
    VM.Standard.E2.1.Micro)
        note "ok   shape=${SHAPE} — 사용자가 베타에서 받아들인 shape입니다" ;;
    VM.Standard.A1.Flex)
        note "ok   shape=${SHAPE} — Ampere A1" ;;
    ""|"?")
        note "FAIL 인스턴스 shape을 읽지 못했습니다"; FAILED=1 ;;
    *)
        note "FAIL shape=${SHAPE} 는 받아들인 shape이 아닙니다 (VM.Standard.E2.1.Micro 또는 VM.Standard.A1.Flex)"; FAILED=1 ;;
esac
# Micro를 받아들인 전제는 할당량이다: 세션당 2 Mbps, 합계 8 Mbps. 지금 coturn이
# 실제로 읽고 있는 설정에서 그 줄만 골라 읽는다(비밀에 해당하는 줄은 읽지 않는다).
CONF="$("${SSH[@]}" 'sudo grep -E "^(max-bps|bps-capacity|min-port|max-port|use-auth-secret)" /etc/coturn/turnserver.conf' 2>/dev/null)"
check_conf() {
    if printf '%s\n' "${CONF}" | grep -qx -- "$1"; then note "ok   $2"; else note "FAIL $2 — 살아 있는 설정에 '$1' 이 없습니다"; FAILED=1; fi
}
check_conf "use-auth-secret" "use-auth-secret (릴레이가 발급한 자격증명만 허용)"
check_conf "max-bps=2000000" "세션당 할당량 2 Mbps"
check_conf "bps-capacity=8000000" "합계 할당량 8 Mbps"
check_conf "min-port=${MIN_PORT}" "중계 포트 최소 ${MIN_PORT}"
check_conf "max-port=${MAX_PORT}" "중계 포트 최대 ${MAX_PORT}"

# R9 ───────────────────────────────────────────────────────────────────────────
step R9 "밖에서 UDP 3478이 닿는지 (Oracle 보안 목록)"
if python3 "${SCRIPT_DIR}/stun-probe.py" "${HOST}" 3478; then
    note "→ 보안 목록이 열려 있습니다. 강제 TURN 경로를 기기에서 시험할 수 있습니다."
else
    FAILED=1
    cat <<'MSG'
  → Oracle 클라우드 보안 목록(VCN)이 UDP 3478을 막고 있습니다. VM 속 coturn과
    OS 방화벽은 정상이니(R4·R5·R6), 남은 것은 보안 목록을 한 번 여는 일뿐입니다.
    OCI 세션은 브라우저 로그인이 필요해 이 자리에서 대신할 수 없습니다. 사용자가:
      oci session authenticate --profile-name mighty --region ap-singapore-1
      bash relay/deploy/oracle/open-turn-port.sh --profile mighty
    그 뒤 이 스크립트를 다시 돌리세요.
MSG
fi

# 요약 ────────────────────────────────────────────────────────────────────────
echo ""
if [ "${FAILED}" -ne 0 ]; then
    echo "✘ REQUIRED 항목이 깨졌습니다. 위 FAIL 줄을 보세요."
    exit 1
fi
echo "✔ R1~R9 전부 통과 — 릴레이·TURN 발급·coturn·방화벽·보안 목록이 살아 있고,"
echo "  베타는 받아들인 ${SHAPE}에서 돕니다."
