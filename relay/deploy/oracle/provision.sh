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

# ── 첫 부팅 설정(cloud-init) ─────────────────────────────────────────────────
# 새 인스턴스는 첫 부팅 때 스스로 저장소를 받아 setup.sh를 돌린다. 그래서 22번
# 포트를 막는 네트워크에서도 SSH 없이 끝난다. 인증서는 DuckDNS가 이 서버를
# 가리키는 순간 Caddy가 알아서 받는다.
USER_DATA="$(mktemp)"; trap 'rm -f "${ERR}" "${USER_DATA}"' EXIT
cat > "${USER_DATA}" <<USERDATA
#!/bin/bash
apt-get update -qq && apt-get install -y -qq git curl
sudo -u ubuntu -H git clone -q --depth 1 ${REPO} /home/ubuntu/MightyClaude
sudo -u ubuntu -H bash /home/ubuntu/MightyClaude/relay/deploy/oracle/setup.sh ${DOMAIN} || true
USERDATA

# ── 인스턴스 ────────────────────────────────────────────────────────────────
INSTANCE="$(q compute instance list --compartment-id "$C" --display-name "${NAME}" --query "data[?\"lifecycle-state\"!='TERMINATED' && \"lifecycle-state\"!='TERMINATING'] | [0].id")"
if [ -z "${INSTANCE}" ]; then
    ADS="$(q iam availability-domain list --compartment-id "$C" --query 'join(`" "`, data[].name)')"
    IMAGE_A1="$(q compute image list --compartment-id "$C" --operating-system "Canonical Ubuntu" --operating-system-version "24.04" --shape VM.Standard.A1.Flex --sort-by TIMECREATED --sort-order DESC --query 'data[0].id')"
    IMAGE_E2="$(q compute image list --compartment-id "$C" --operating-system "Canonical Ubuntu" --operating-system-version "24.04" --shape VM.Standard.E2.1.Micro --sort-by TIMECREATED --sort-order DESC --query 'data[0].id')"
    # 무료 인스턴스는 자주 "용량 없음"(Out of host capacity)이고, 연달아 시도하면
    # Oracle이 잠시 막는다(429 TooManyRequests). 둘 다 멈출 이유가 아니라 기다렸다
    # 다시 할 이유다. 라운드마다 A1 → E2.1.Micro 순으로 시도한다.
    PAUSE="${PROVISION_PAUSE:-1}"   # 시험용 배율(0이면 기다리지 않는다)
    ROUNDS="${PROVISION_ROUNDS:-12}"
    for ROUND in $(seq 1 "${ROUNDS}"); do
        for SHAPE in VM.Standard.A1.Flex VM.Standard.E2.1.Micro; do
            if [ "${SHAPE}" = "VM.Standard.A1.Flex" ]; then IMAGE="${IMAGE_A1}"; SHAPE_ARGS=(--shape-config '{"ocpus":1,"memoryInGBs":6}')
            else IMAGE="${IMAGE_E2}"; SHAPE_ARGS=(); fi
            [ -n "${IMAGE}" ] || continue
            for AD in ${ADS}; do
                say "인스턴스를 만듭니다: ${SHAPE} (${AD}) — ${ROUND}/${ROUNDS}회차"
                if INSTANCE="$("${OCI[@]}" compute instance launch --compartment-id "$C" --availability-domain "${AD}" \
                        --shape "${SHAPE}" ${SHAPE_ARGS[@]+"${SHAPE_ARGS[@]}"} --image-id "${IMAGE}" --subnet-id "${SUBNET}" \
                        --assign-public-ip false --display-name "${NAME}" --ssh-authorized-keys-file "${SSH_KEY}.pub" --user-data-file "${USER_DATA}" \
                        --wait-for-state RUNNING --query data.id --raw-output 2>"${ERR}")"; then
                    break 3
                fi
                INSTANCE=""
                if grep -qiE "TooManyRequests|\"status\": 429" "${ERR}"; then
                    echo "  요청이 너무 잦다고 Oracle이 잠시 막았습니다. 90초 쉬고 이어 갑니다."; sleep $((90 * PAUSE))
                elif grep -qi "capacity" "${ERR}"; then
                    echo "  지금 이 종류는 용량이 없습니다."; sleep $((20 * PAUSE))
                else
                    cat "${ERR}" >&2; fail "인스턴스를 만들지 못했습니다."
                fi
            done
        done
        [ "${ROUND}" -lt "${ROUNDS}" ] && { echo "  2분 뒤 다시 시도합니다 (Ctrl+C로 멈춰도 다음에 이어집니다)."; sleep $((120 * PAUSE)); }
    done
    [ -n "${INSTANCE}" ] || fail "이 리전의 무료 인스턴스에 지금 자리가 나지 않습니다. 몇 시간 뒤 같은 명령을 다시 돌리세요(만든 네트워크는 그대로 이어 씁니다)."
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
healthy() { [ "$(curl -fsS --max-time 5 "https://${DOMAIN}/healthz" 2>/dev/null)" = "ok" ]; }
SELF_SETUP="$(q compute instance get --instance-id "${INSTANCE}" --query 'data.metadata."user_data"')"
if [ -n "${SELF_SETUP}" ]; then
    # 첫 부팅 설정이 있는 인스턴스: SSH 없이 https://<이름>/healthz만 기다린다.
    say "서버가 스스로 설정을 마칠 때까지 기다립니다 (보통 5~10분)"
    for i in $(seq 1 90); do
        healthy && break
        [ $((i % 6)) -eq 0 ] && printf '  %d분 지남\n' $((i / 6))
        sleep 10
    done
    healthy || fail "15분이 지나도 https://${DOMAIN}/healthz가 ok가 아닙니다. 22번이 열린 네트워크에서: ssh -i ${SSH_KEY} ubuntu@${IP} 'sudo tail -50 /var/log/cloud-init-output.log'"
else
    # 첫 부팅 설정 없이 만든 예전 인스턴스: SSH로 setup.sh를 돌린다.
    # BatchMode: 키가 안 맞을 때 비밀번호를 묻고 멈추지 않는다. IdentitiesOnly: 에이전트의
    # 다른 키를 먼저 내밀다 "Too many authentication failures"로 끊기지 않는다.
    SSH=(ssh -i "${SSH_KEY}" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 "ubuntu@${IP}")
    say "SSH가 열릴 때까지 기다립니다 (새 인스턴스는 부팅에 2~5분 걸립니다)"
    SSH_OK=""
    for i in $(seq 1 40); do
        if "${SSH[@]}" true 2>"${ERR}"; then SSH_OK=1; break; fi
        printf '  %2d/40  %s\n' "$i" "$(tail -1 "${ERR}")"
        sleep 7
    done
    if [ -z "${SSH_OK}" ]; then
        grep -qi "timed out" "${ERR}" && ! nc -z -G 5 github.com 22 2>/dev/null && \
            fail "이 네트워크가 바깥으로 나가는 22번 포트를 막고 있습니다(github.com:22도 막힘). 휴대폰 핫스팟 등 다른 네트워크에서 다시 돌리세요."
        fail "ubuntu@${IP}에 SSH로 접속하지 못했습니다. 위 오류가 'timed out'이면 보안 목록·라우팅, 'Permission denied'면 키, 'refused'면 아직 부팅 중입니다."
    fi
    say "서버에서 setup.sh를 돌립니다"
    "${SSH[@]}" "cloud-init status --wait >/dev/null 2>&1 || true
        command -v git >/dev/null || { sudo apt-get update -qq && sudo apt-get install -y -qq git; }
        if [ -d MightyClaude ]; then git -C MightyClaude pull --ff-only -q; else git clone -q --depth 1 ${REPO}; fi
        bash MightyClaude/relay/deploy/oracle/setup.sh ${DOMAIN}"
fi

echo ""
echo "✔ 끝. Mac → 설정 → 모바일 리모트 → 릴레이: wss://${DOMAIN}"
echo "  서버 접속: ssh -i ${SSH_KEY} ubuntu@${IP}"
