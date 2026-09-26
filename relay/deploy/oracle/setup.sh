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
    echo "▶ firewalld에서 80·443을 엽니다..."
    sudo firewall-cmd --permanent --add-port=80/tcp --add-port=443/tcp --add-port=443/udp >/dev/null
    sudo firewall-cmd --reload >/dev/null
elif command -v iptables &>/dev/null; then
    echo "▶ iptables에서 80·443을 엽니다..."
    for rule in "-p tcp --dport 80" "-p tcp --dport 443" "-p udp --dport 443"; do
        # shellcheck disable=SC2086
        sudo iptables -C INPUT $rule -j ACCEPT 2>/dev/null || sudo iptables -I INPUT 1 $rule -j ACCEPT
    done
    if command -v netfilter-persistent &>/dev/null; then sudo netfilter-persistent save >/dev/null; fi
fi

# ── 스택 시작 ───────────────────────────────────────────────────────────────
printf 'RELAY_DOMAIN=%s\n' "${DOMAIN}" > .env
echo "▶ 릴레이와 Caddy를 빌드하고 시작합니다 (첫 실행은 몇 분 걸립니다)..."
sudo docker compose up -d --build

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
