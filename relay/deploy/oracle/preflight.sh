#!/usr/bin/env bash
# 용법: bash relay/deploy/oracle/preflight.sh [--host <주소>] [--key <ssh 키>] [--min-mbps <숫자>]
#
# 배포 전 점검(pre-flight). TURN을 올려도 되는 서버인지 확인한다.
#   1) Oracle 인스턴스 메타데이터에서 shape·OCPU·메모리·대역폭을 읽는다
#      (OCI CLI 세션이 필요 없다. 서버가 자기 자신을 169.254.169.254에 물어본다).
#   2A) shape이 Ampere A1(VM.Standard.A1.Flex)이면 통과.
#   2B) A1이 아니면 — Micro 등 — shape 이름만 믿지 않고 실제 외부 전송 속도를
#       잰다. 이 관문이 실제로 묻는 것은 "이 서버가 TURN 중계를 감당하는가"이고,
#       그 요구는 coturn 전체 할당량 8 Mbps다. 측정값이 --min-mbps(기본 16,
#       8 Mbps의 2배 여유)를 넘으면 경고와 함께 통과하고, 못 넘으면 멈춘다.
#       100 Mbps 미만이면 사용자가 걱정한 "~50 Mbps로 묶인 Micro"임을 측정값과
#       함께 분명히 알린다. A1으로 올리는 것은 OCI 로그인이 필요한 별도 작업이다.
# 종료 코드 0이면 배포해도 된다.
set -uo pipefail

HOST="mightyclaude.duckdns.org"
KEY="${HOME}/.ssh/mightyclaude-relay"
MIN_MBPS=16          # coturn 전체 할당량 8 Mbps의 2배 여유
CAP_WARN_MBPS=100    # 이 아래면 "묶인 Micro"로 알린다
SIZE_BYTES=25000000

while [ $# -gt 0 ]; do
    case "$1" in
        --host) HOST="$2"; shift 2 ;;
        --key) KEY="$2"; shift 2 ;;
        --min-mbps) MIN_MBPS="$2"; shift 2 ;;
        -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
        *) echo "모르는 인수: $1" >&2; exit 1 ;;
    esac
done

SSH=(ssh -i "${KEY}" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "ubuntu@${HOST}")

fail() { printf '✘ %s\n' "$*" >&2; exit 1; }

[ -f "${KEY}" ] || fail "SSH 키가 없습니다: ${KEY}"

echo "▶ pre-flight: ${HOST}"

# ── 1) 인스턴스 메타데이터 ──────────────────────────────────────────────────
META="$("${SSH[@]}" 'curl -fsS -H "Authorization: Bearer Oracle" --max-time 10 http://169.254.169.254/opc/v2/instance/' 2>/dev/null)" \
    || fail "인스턴스 메타데이터를 읽지 못했습니다(SSH 또는 메타데이터 서비스 실패)."

read -r SHAPE OCPUS MEM_GB BW_GBPS REGION <<EOF2
$(printf '%s' "${META}" | python3 -c '
import json,sys
d = json.load(sys.stdin)
c = d.get("shapeConfig") or {}
print(d.get("shape","?"), c.get("ocpus","?"), c.get("memoryInGBs","?"),
      c.get("networkingBandwidthInGbps","?"), d.get("region","?"))
')
EOF2
[ -n "${SHAPE}" ] || fail "shape을 읽지 못했습니다."

printf '  shape=%s  ocpus=%s  memory=%sGB  대역폭=%sGbps  region=%s\n' \
    "${SHAPE}" "${OCPUS}" "${MEM_GB}" "${BW_GBPS}" "${REGION}"

if [ "${SHAPE}" = "VM.Standard.A1.Flex" ]; then
    echo "✔ pre-flight 통과: Ampere A1 shape입니다."
    exit 0
fi

# ── 2B) A1이 아니면 실제 속도를 잰다 ────────────────────────────────────────
printf '⚠ A1이 아닙니다(%s). 실제 외부 전송 속도를 재서 판단합니다...\n' "${SHAPE}"
SPEED_BPS="$("${SSH[@]}" "curl -o /dev/null -s -w '%{speed_download}' --max-time 90 'https://speed.cloudflare.com/__down?bytes=${SIZE_BYTES}'" 2>/dev/null)"
SPEED_BPS="${SPEED_BPS%%.*}"
[ -n "${SPEED_BPS}" ] && [ "${SPEED_BPS}" -gt 0 ] 2>/dev/null || fail "속도 측정에 실패했습니다(측정값 없음)."
MBPS="$(awk -v b="${SPEED_BPS}" 'BEGIN{printf "%.1f", b*8/1000000}')"
printf '  측정 외부 전송 속도: %s Mbps (기준 %s Mbps)\n' "${MBPS}" "${MIN_MBPS}"

if awk -v m="${MBPS}" -v t="${CAP_WARN_MBPS}" 'BEGIN{exit !(m<t)}'; then
    printf '⚠ 이 서버는 shape 이름(%s)과 메타데이터(%s Gbps)와 달리 실제로 %s Mbps에서
' \
        "${SHAPE}" "${BW_GBPS}" "${MBPS}"
    printf '  묶여 있습니다. 사용자가 걱정한 "~50 Mbps Micro"가 맞습니다.
'
fi

if awk -v m="${MBPS}" -v t="${MIN_MBPS}" 'BEGIN{exit !(m>t)}'; then
    cat <<MSG
✔ pre-flight 통과(조건부, shape은 A1 아님):
  shape=${SHAPE}, 측정 ${MBPS} Mbps > 기준 ${MIN_MBPS} Mbps.
  TURN 중계가 쓰는 대역폭(세션당 2 Mbps·합계 8 Mbps)에는 충분합니다.
  A1으로 올리려면 OCI 로그인 뒤 provision.sh를 A1 용량이 날 때 다시 돌리세요:
    oci session authenticate --profile-name mighty --region ap-singapore-1
MSG
    exit 0
fi

fail "pre-flight 실패: ${SHAPE}의 측정 속도 ${MBPS} Mbps가 기준 ${MIN_MBPS} Mbps 이하입니다. TURN을 올리지 마세요."
