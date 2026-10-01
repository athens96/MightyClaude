#!/usr/bin/env bash
# 용법: bash relay/deploy/oracle/setup.sh <이름>.duckdns.org
#
# Oracle Cloud 인스턴스(Ubuntu 또는 Oracle Linux, arm64·amd64 모두)에 Docker를
# 설치하고, OS 방화벽에서 80·443을 연 뒤, 릴레이와 Caddy를 띄운다. 여러 번
# 돌려도 된다. Oracle Cloud 보안 목록의 80·443은 콘솔에서 따로 연다
# (docs/relay-oracle.md 4단계).
set -euo pipefail

DOMAIN="${1:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${SCRIPT_DIR}"

if [ -z "${DOMAIN}" ] && [ -f .env ]; then
    DOMAIN="$(grep -E '^RELAY_DOMAIN=' .env | cut -d= -f2-)"
fi
if [ -z "${DOMAIN}" ]; then
    echo "용법: bash $0 <이름>.duckdns.org" >&2
    exit 1
fi

# TURN 중계(미디어) 포트 범위. turnserver.conf의 min-port·max-port와 같아야 한다.
TURN_MEDIA_PORTS="${TURN_MEDIA_PORTS:-49160:49200}"

# ── Docker 설치 ─────────────────────────────────────────────────────────────
if ! command -v docker &>/dev/null; then
    echo "▶ Docker를 설치합니다..."
    if command -v apt-get &>/dev/null; then
        curl -fsSL https://get.docker.com | sudo sh
    elif command -v dnf &>/dev/null; then
        sudo dnf -y install dnf-utils
        sudo dnf config-manager --add-repo https://download.docker.com/linux/rhel/docker-ce.repo
        sudo dnf -y install docker-ce docker-ce-cli containerd.io docker-compose-plugin
    else
        echo "apt-get도 dnf도 없는 이미지입니다. Ubuntu 이미지로 인스턴스를 만드세요." >&2
        exit 1
    fi
    sudo systemctl enable --now docker
fi

# ── OS 방화벽 개방 ──────────────────────────────────────────────────────────
# Oracle의 Ubuntu 이미지는 iptables가, Oracle Linux 이미지는 firewalld가 막는다.
if command -v firewall-cmd &>/dev/null && sudo firewall-cmd --state &>/dev/null; then
    echo "▶ firewalld에서 80·443·3478·${TURN_MEDIA_PORTS}을 엽니다..."
    sudo firewall-cmd --permanent --add-port=80/tcp --add-port=443/tcp --add-port=443/udp \
        --add-port=3478/udp --add-port=3478/tcp --add-port=${TURN_MEDIA_PORTS}/udp >/dev/null
    sudo firewall-cmd --reload >/dev/null
elif command -v iptables &>/dev/null; then
    echo "▶ iptables에서 80·443·3478·${TURN_MEDIA_PORTS}(TURN 중계 포트)를 엽니다..."
    for rule in "-p tcp --dport 80" "-p tcp --dport 443" "-p udp --dport 443" "-p udp --dport 3478" "-p tcp --dport 3478" "-p udp --dport ${TURN_MEDIA_PORTS}"; do
        # shellcheck disable=SC2086
        sudo iptables -C INPUT $rule -j ACCEPT 2>/dev/null || sudo iptables -I INPUT 1 $rule -j ACCEPT
    done
    if command -v netfilter-persistent &>/dev/null; then sudo netfilter-persistent save >/dev/null; fi
fi

# ── 스택 시작 ───────────────────────────────────────────────────────────────
# ── TURN 시크릿 ─────────────────────────────────────────────────────────────
# TURN_SECRET이 이미 .env에 있으면 다시 만들지 않는다(재배포에 안전).
TURN_SECRET_VAL=""
if [ -f .env ]; then
    TURN_SECRET_VAL="$(grep -E '^TURN_SECRET=' .env | cut -d= -f2-)"
fi
if [ -z "${TURN_SECRET_VAL}" ]; then
    TURN_SECRET_VAL="$(openssl rand -hex 32)"
    echo "▶ TURN 시크릿을 새로 만들었습니다."
fi

printf 'RELAY_DOMAIN=%s\nTURN_SECRET=%s\n' "${DOMAIN}" "${TURN_SECRET_VAL}" > .env

# ── coturn 설정의 플레이스홀더를 실제 값으로 치환하고 호스트에 기록 ─────────
# compose.yaml이 /etc/coturn/turnserver.conf를 컨테이너에 마운트한다.
# 시크릿과 IP를 저장소에 남기지 않으려고 별도 경로에 쓴다.
INTERNAL_IP="$(hostname -I | awk '{print $1}')"
EXTERNAL_IP="$(curl -fsS --max-time 5 https://ifconfig.me 2>/dev/null || curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || echo "${INTERNAL_IP}")"
echo "▶ 외부 IP: ${EXTERNAL_IP}, 내부 IP: ${INTERNAL_IP}"
sudo mkdir -p /etc/coturn
sed -e "s/\${TURN_SECRET}/${TURN_SECRET_VAL}/g" \
    -e "s/\${EXTERNAL_IP}/${EXTERNAL_IP}/g" \
    -e "s/\${INTERNAL_IP}/${INTERNAL_IP}/g" \
    turnserver.conf | sudo tee /etc/coturn/turnserver.conf >/dev/null
# coturn 공식 이미지는 nobody:nogroup(65534)으로 돌기 때문에 600 root 파일은
# 읽지 못하고, 그러면 coturn이 경고 한 줄만 남기고 기본 설정(인증 없는 공개
# 중계!)으로 떠 버린다. 그룹만 읽게 열어 준다.
sudo chown root:65534 /etc/coturn/turnserver.conf
sudo chmod 640 /etc/coturn/turnserver.conf
echo "▶ /etc/coturn/turnserver.conf 기록 완료"

echo "▶ 릴레이와 Caddy와 coturn을 빌드하고 시작합니다 (첫 실행은 몇 분 걸립니다)..."
sudo docker compose up -d --build

# 설정 파일은 바인드 마운트라 compose가 변경을 알아채지 못한다. 방금 다시 쓴
# 설정을 확실히 읽히게 coturn만 재시작한다(릴레이와 Caddy는 건드리지 않는다).
echo "▶ coturn을 재시작해 새 설정을 읽힙니다..."
sudo docker compose restart coturn

# ── coturn이 설정 파일을 실제로 읽었는지 확인 ───────────────────────────────
# 읽지 못하면 coturn은 멈추지 않고 기본값(인증 없는 공개 중계)으로 뜬다. 로그를
# 뒤지는 대신 행동으로 확인한다: 자격증명 없는 Allocate는 401이어야 한다.
sleep 3
if ! python3 turn-allocate-check.py --no-credentials "${INTERNAL_IP}" 3478; then
    echo "✘ coturn이 인증을 요구하지 않습니다(설정 파일을 못 읽은 공개 중계 상태)." >&2
    echo "  /etc/coturn/turnserver.conf 권한을 확인하세요. coturn을 멈춥니다." >&2
    sudo docker compose stop coturn || true
    exit 1
fi

# ── 확인 ────────────────────────────────────────────────────────────────────
echo "▶ 인증서를 받고 응답하는지 기다립니다..."
for _ in $(seq 1 30); do
    if [ "$(curl -fsS --max-time 5 "https://${DOMAIN}/healthz" 2>/dev/null)" = "ok" ]; then
        echo ""
        echo "✔ 완료. Mac의 릴레이 주소: wss://${DOMAIN}"
        exit 0
    fi
    sleep 5
done
echo ""
echo "✘ https://${DOMAIN}/healthz 가 아직 ok를 돌려주지 않습니다." >&2
echo "  DuckDNS의 IP, 보안 목록의 80·443, 'sudo docker compose logs caddy'를 확인하세요." >&2
exit 1
