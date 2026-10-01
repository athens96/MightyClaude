#!/usr/bin/env bash
# 용법: bash relay/deploy/oracle/open-turn-port.sh [--profile <oci 프로필>]
#
# 이미 배포된 Oracle Cloud 릴레이 VM의 VCN 보안 목록에 UDP/TCP 3478(TURN)을 엽니다.
# 먼저 OCI 세션이 살아있어야 합니다:
#   oci session authenticate --profile-name mighty --region ap-singapore-1
set -euo pipefail

PROFILE="mighty"
NAME="mightyclaude-relay"

while [ $# -gt 0 ]; do
    case "$1" in
        --profile) PROFILE="$2"; shift 2 ;;
        -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
        *) echo "모르는 인수: $1" >&2; exit 1 ;;
    esac
done

OCI=(oci --profile "${PROFILE}" --auth security_token)

if ! oci session validate --profile "${PROFILE}" >/dev/null 2>&1; then
    echo "OCI 세션이 없거나 만료됐습니다." >&2
    echo "먼저 실행: oci session authenticate --profile-name ${PROFILE} --region ap-singapore-1" >&2
    exit 1
fi

CONFIG="${OCI_CLI_CONFIG_FILE:-${HOME}/.oci/config}"
TENANCY="$(awk -v p="[${PROFILE}]" '$0==p{f=1;next} /^\[/{f=0} f&&/^tenancy[ ]*=/{sub(/^tenancy[ ]*=[ ]*/,"");print;exit}' "${CONFIG}")"
C="${TENANCY}"

q() {
    local out
    if ! out="$("${OCI[@]}" "$@" --raw-output 2>/dev/null)"; then echo ""; return 0; fi
    [ "${out}" = "null" ] && out=""
    printf '%s' "${out}"
}

VCN="$(q network vcn list --compartment-id "$C" --display-name "${NAME}-vcn" --query "data[?\"lifecycle-state\"=='AVAILABLE'] | [0].id")"
[ -n "${VCN}" ] || { echo "VCN을 찾지 못했습니다(${NAME}-vcn). provision.sh로 먼저 VM을 만드세요." >&2; exit 1; }

SL="$(q network vcn get --vcn-id "${VCN}" --query 'data."default-security-list-id"')"
[ -n "${SL}" ] || { echo "보안 목록 ID를 찾지 못했습니다." >&2; exit 1; }

echo "▶ VCN: ${VCN}"
echo "▶ 보안 목록: ${SL}"

tcp() { printf '{"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":%s,"max":%s}}}' "$1" "$1"; }
udp() { printf '{"protocol":"17","source":"0.0.0.0/0","udpOptions":{"destinationPortRange":{"min":%s,"max":%s}}}' "$1" "${2:-$1}"; }
INGRESS="[$(tcp 22),$(tcp 80),$(tcp 443),$(udp 443),$(tcp 3478),$(udp 3478),$(udp 49160 49200)]"

echo "▶ 보안 목록에 UDP/TCP 3478과 TURN 미디어 포트(UDP 49160-49200)를 추가합니다..."
"${OCI[@]}" network security-list update --security-list-id "${SL}" --force \
    --ingress-security-rules "${INGRESS}" \
    --egress-security-rules '[{"protocol":"all","destination":"0.0.0.0/0"}]' >/dev/null

echo "✔ UDP/TCP 3478과 UDP 49160-49200 열림. TURN 서버가 외부에서 접근 가능합니다."
echo "  테스트: nc -u -z -w 3 mightyclaude.duckdns.org 3478 && echo OK"
