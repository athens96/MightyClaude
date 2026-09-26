#!/usr/bin/env bash
# 용법: bash relay/deploy/oracle/provision.sh --domain <이름>.duckdns.org [--profile <oci 프로필>] [--name <이름 접두사>]
#
# Mac에서 돌린다. Oracle Cloud CLI(`oci`)로 릴레이 서버를 통째로 만든다:
# VCN·인터넷 게이트웨이·보안 규칙(22/80/443)·서브넷 → Ubuntu 24.04 인스턴스
# (Ampere A1 1 OCPU/6 GB, 용량이 없으면 AMD E2.1.Micro) → 예약 공인 IP → DuckDNS
# 확인 → SSH로 setup.sh 실행. 같은 이름의 자원이 이미 있으면 다시 만들지 않으니
# 여러 번 돌려도 된다.
#
# 먼저 한 번:  brew install oci-cli
#              oci session authenticate --region ap-singapore-1 --profile-name mighty
# 로그인 세션은 브라우저로 받은 것을 쓰고(--auth security_token), 이 스크립트는
# 키·토큰을 읽거나 출력하지 않는다. SSH 키는 ~/.ssh/<이름>에 새로 만든다.
set -euo pipefail

PROFILE="DEFAULT"
DOMAIN=""
NAME="mightyclaude-relay"
REPO="https://github.com/athens96/MightyClaude.git"

while [ $# -gt 0 ]; do
    case "$1" in
        --profile) PROFILE="$2"; shift 2 ;;
        --domain) DOMAIN="$2"; shift 2 ;;
        --name) NAME="$2"; shift 2 ;;
        -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
        *) echo "모르는 인수: $1" >&2; exit 1 ;;
    esac
done
[ -n "${DOMAIN}" ] || { echo "--domain <이름>.duckdns.org 가 필요합니다." >&2; exit 1; }
command -v oci >/dev/null || { echo "oci가 없습니다: brew install oci-cli" >&2; exit 1; }

SSH_KEY="${HOME}/.ssh/${NAME}"
OCI=(oci --profile "${PROFILE}" --auth security_token)
ERR="$(mktemp)"; trap 'rm -f "${ERR}"' EXIT

say() { printf '▶ %s\n' "$*"; }
fail() { printf '✘ %s\n' "$*" >&2; exit 1; }

# 값 하나를 읽는다. 결과가 비었으면 빈 문자열, 명령이 실패하면 멈춘다.
q() {
    local out
    if ! out="$("${OCI[@]}" "$@" --raw-output 2>"${ERR}")"; then
        grep -q "Query returned empty result" "${ERR}" && { echo ""; return 0; }
        cat "${ERR}" >&2; fail "oci $* 실패"
    fi
    [ "${out}" = "null" ] && out=""
    printf '%s' "${out}"
}

# ── 로그인 세션 확인 ────────────────────────────────────────────────────────
if ! oci session validate --profile "${PROFILE}" >/dev/null 2>&1; then
    fail "로그인 세션이 없거나 만료됐습니다: oci session authenticate --profile-name ${PROFILE} (리전은 가입한 홈 리전)"
fi
CONFIG="${OCI_CLI_CONFIG_FILE:-${HOME}/.oci/config}"
TENANCY="$(awk -v p="[${PROFILE}]" '$0==p{f=1;next} /^\[/{f=0} f&&/^tenancy[ ]*=/{sub(/^tenancy[ ]*=[ ]*/,"");print;exit}' "${CONFIG}")"
[ -n "${TENANCY}" ] || fail "${CONFIG}의 [${PROFILE}]에서 tenancy를 찾지 못했습니다."
C="${TENANCY}"   # 루트 컴파트먼트

# ── 네트워크 ────────────────────────────────────────────────────────────────
VCN="$(q network vcn list --compartment-id "$C" --display-name "${NAME}-vcn" --query "data[?\"lifecycle-state\"=='AVAILABLE'] | [0].id")"
if [ -z "${VCN}" ]; then
    say "VCN을 만듭니다"
    VCN="$(q network vcn create --compartment-id "$C" --display-name "${NAME}-vcn" --cidr-blocks '["10.20.0.0/16"]' --dns-label mcrelay --wait-for-state AVAILABLE --query data.id)"
fi
IGW="$(q network internet-gateway list --compartment-id "$C" --vcn-id "${VCN}" --query "data[?\"display-name\"=='${NAME}-igw'] | [0].id")"
if [ -z "${IGW}" ]; then
    say "인터넷 게이트웨이를 만듭니다"
    IGW="$(q network internet-gateway create --compartment-id "$C" --vcn-id "${VCN}" --is-enabled true --display-name "${NAME}-igw" --wait-for-state AVAILABLE --query data.id)"
fi
RT="$(q network vcn get --vcn-id "${VCN}" --query 'data."default-route-table-id"')"
SL="$(q network vcn get --vcn-id "${VCN}" --query 'data."default-security-list-id"')"
say "라우팅과 보안 규칙(22·80·443)을 맞춥니다"
"${OCI[@]}" network route-table update --rt-id "${RT}" --force \
    --route-rules "[{\"destination\":\"0.0.0.0/0\",\"destinationType\":\"CIDR_BLOCK\",\"networkEntityId\":\"${IGW}\"}]" >/dev/null
tcp() { printf '{"protocol":"6","source":"0.0.0.0/0","tcpOptions":{"destinationPortRange":{"min":%s,"max":%s}}}' "$1" "$1"; }
INGRESS="[$(tcp 22),$(tcp 80),$(tcp 443),{\"protocol\":\"17\",\"source\":\"0.0.0.0/0\",\"udpOptions\":{\"destinationPortRange\":{\"min\":443,\"max\":443}}}]"
"${OCI[@]}" network security-list update --security-list-id "${SL}" --force \
    --ingress-security-rules "${INGRESS}" \
    --egress-security-rules '[{"protocol":"all","destination":"0.0.0.0/0"}]' >/dev/null
SUBNET="$(q network subnet list --compartment-id "$C" --vcn-id "${VCN}" --display-name "${NAME}-subnet" --query "data[?\"lifecycle-state\"=='AVAILABLE'] | [0].id")"
if [ -z "${SUBNET}" ]; then
    say "서브넷을 만듭니다"
    SUBNET="$(q network subnet create --compartment-id "$C" --vcn-id "${VCN}" --cidr-block 10.20.0.0/24 --display-name "${NAME}-subnet" --dns-label relay --wait-for-state AVAILABLE --query data.id)"
fi

# ── SSH 키 ──────────────────────────────────────────────────────────────────
if [ ! -f "${SSH_KEY}" ]; then
    say "SSH 키를 만듭니다: ${SSH_KEY}"
    mkdir -p "${HOME}/.ssh"; chmod 700 "${HOME}/.ssh"
    ssh-keygen -q -t ed25519 -N "" -C "${NAME}" -f "${SSH_KEY}"
fi

# ── 인스턴스 ────────────────────────────────────────────────────────────────
INSTANCE="$(q compute instance list --compartment-id "$C" --display-name "${NAME}" --query "data[?\"lifecycle-state\"!='TERMINATED' && \"lifecycle-state\"!='TERMINATING'] | [0].id")"
if [ -z "${INSTANCE}" ]; then
    ADS="$(q iam availability-domain list --compartment-id "$C" --query 'join(`" "`, data[].name)')"
    for SHAPE in VM.Standard.A1.Flex VM.Standard.E2.1.Micro; do
        IMAGE="$(q compute image list --compartment-id "$C" --operating-system "Canonical Ubuntu" --operating-system-version "24.04" --shape "${SHAPE}" --sort-by TIMECREATED --sort-order DESC --query 'data[0].id')"
        [ -n "${IMAGE}" ] || continue
        SHAPE_ARGS=()
        [ "${SHAPE}" = "VM.Standard.A1.Flex" ] && SHAPE_ARGS=(--shape-config '{"ocpus":1,"memoryInGBs":6}')
        for AD in ${ADS}; do
            say "인스턴스를 만듭니다: ${SHAPE} (${AD})"
            if INSTANCE="$("${OCI[@]}" compute instance launch --compartment-id "$C" --availability-domain "${AD}" \
                    --shape "${SHAPE}" ${SHAPE_ARGS[@]+"${SHAPE_ARGS[@]}"} --image-id "${IMAGE}" --subnet-id "${SUBNET}" \
                    --assign-public-ip false --display-name "${NAME}" --ssh-authorized-keys-file "${SSH_KEY}.pub" \
                    --wait-for-state RUNNING --query data.id --raw-output 2>"${ERR}")"; then
                break 2
            fi
            INSTANCE=""
            if grep -qi "capacity" "${ERR}"; then echo "  용량이 없습니다. 다음 선택지로 넘어갑니다."; else cat "${ERR}" >&2; fail "인스턴스를 만들지 못했습니다."; fi
        done
    done
    [ -n "${INSTANCE}" ] || fail "A1과 E2.1.Micro 모두 지금 용량이 없습니다. 잠시 뒤 다시 돌리세요."
else
    say "이미 있는 인스턴스를 씁니다"
fi

# ── 예약 공인 IP ────────────────────────────────────────────────────────────
PIP="$(q network public-ip list --compartment-id "$C" --scope REGION --lifetime RESERVED --query "data[?\"display-name\"=='${NAME}-ip'] | [0].id")"
if [ -z "${PIP}" ]; then
    say "예약 공인 IP를 만듭니다"
    PIP="$(q network public-ip create --compartment-id "$C" --lifetime RESERVED --display-name "${NAME}-ip" --query data.id)"
fi
VNIC="$(q compute instance list-vnics --instance-id "${INSTANCE}" --query 'data[0].id')"
PRIV="$(q network private-ip list --vnic-id "${VNIC}" --query 'data[0].id')"
if [ "$(q network public-ip get --public-ip-id "${PIP}" --query 'data."private-ip-id"')" != "${PRIV}" ]; then
    say "공인 IP를 인스턴스에 붙입니다"
    "${OCI[@]}" network public-ip update --public-ip-id "${PIP}" --private-ip-id "${PRIV}" --wait-for-state ASSIGNED >/dev/null
fi
IP="$(q network public-ip get --public-ip-id "${PIP}" --query 'data."ip-address"')"

# ── DuckDNS ─────────────────────────────────────────────────────────────────
resolved() { dig +short "${DOMAIN}" A 2>/dev/null | tail -1; }
if [ "$(resolved)" != "${IP}" ]; then
    echo ""
    echo "  서버 공인 IP: ${IP}"
    echo "  duckdns.org에서 ${DOMAIN%%.duckdns.org}의 current ip에 위 주소를 넣고 update ip를 누르세요."
    read -r -p "  넣었으면 Enter… " _
    say "${DOMAIN}이 ${IP}를 가리킬 때까지 기다립니다"
    for _ in $(seq 1 60); do [ "$(resolved)" = "${IP}" ] && break; sleep 5; done
    [ "$(resolved)" = "${IP}" ] || fail "${DOMAIN}이 아직 ${IP}가 아닙니다($(resolved)). DuckDNS를 확인하고 다시 돌리세요."
fi

# ── 서버 설정 ───────────────────────────────────────────────────────────────
SSH=(ssh -i "${SSH_KEY}" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "ubuntu@${IP}")
say "SSH가 열릴 때까지 기다립니다"
for _ in $(seq 1 60); do "${SSH[@]}" true 2>/dev/null && break; sleep 5; done
"${SSH[@]}" true 2>/dev/null || fail "ubuntu@${IP}에 SSH로 접속하지 못했습니다."
say "서버에서 setup.sh를 돌립니다"
"${SSH[@]}" "cloud-init status --wait >/dev/null 2>&1 || true
    command -v git >/dev/null || { sudo apt-get update -qq && sudo apt-get install -y -qq git; }
    if [ -d MightyClaude ]; then git -C MightyClaude pull --ff-only -q; else git clone -q --depth 1 ${REPO}; fi
    bash MightyClaude/relay/deploy/oracle/setup.sh ${DOMAIN}"

echo ""
echo "✔ 끝. Mac → 설정 → 모바일 리모트 → 릴레이: wss://${DOMAIN}"
echo "  서버 접속: ssh -i ${SSH_KEY} ubuntu@${IP}"
