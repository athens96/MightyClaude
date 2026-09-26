#!/usr/bin/env bash
# Oracle Linux 8 (Ampere A1, arm64) — Docker + relay stack installer
# Oracle Cloud 보안 목록에서 포트 80/443(TCP)도 미리 열어 두세요 (3단계 참고).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ── Docker CE 설치 ──────────────────────────────────────────────────────────
if ! command -v docker &>/dev/null; then
    echo "▶ Docker CE를 설치합니다..."
    sudo dnf -y install dnf-utils
    sudo dnf config-manager --add-repo \
        https://download.docker.com/linux/rhel/docker-ce.repo
    sudo dnf -y install docker-ce docker-ce-cli containerd.io docker-compose-plugin
    sudo systemctl enable --now docker
    sudo usermod -aG docker "${USER}"
    echo "✔ Docker 설치 완료."
    echo "  이 터미널에서 계속하려면 'newgrp docker'를 실행한 뒤 스크립트를 다시 실행하세요."
    exit 0
fi

# ── OS 방화벽 개방 ──────────────────────────────────────────────────────────
if command -v firewall-cmd &>/dev/null; then
    echo "▶ 방화벽에 포트 80, 443을 엽니다..."
    sudo firewall-cmd --permanent --add-port=80/tcp
    sudo firewall-cmd --permanent --add-port=443/tcp
    sudo firewall-cmd --permanent --add-port=443/udp
    sudo firewall-cmd --reload
    echo "✔ 방화벽 설정 완료."
fi

# ── .env 파일 확인 ──────────────────────────────────────────────────────────
cd "${SCRIPT_DIR}"
if [ ! -f .env ]; then
    cat > .env <<'EOF'
# DuckDNS 토큰 — https://www.duckdns.org 로그인 후 상단에서 복사
DUCKDNS_TOKEN=your-duckdns-token-here

# DuckDNS 서브도메인 전체 주소 (예: myrelay.duckdns.org)
RELAY_DOMAIN=myrelay.duckdns.org
EOF
    echo ""
    echo "⚠  .env 파일을 만들었습니다."
    echo "   DUCKDNS_TOKEN과 RELAY_DOMAIN을 채운 뒤 이 스크립트를 다시 실행하세요."
    exit 0
fi

# ── 스택 빌드 & 시작 ─────────────────────────────────────────────────────────
echo "▶ 컨테이너를 빌드하고 시작합니다 (첫 실행은 몇 분 걸릴 수 있습니다)..."
docker compose up -d --build

DOMAIN="$(grep -E '^RELAY_DOMAIN=' .env | cut -d= -f2)"
echo ""
echo "✔ 완료! 잠시 후 아래 주소에서 'ok'가 나오면 정상입니다:"
echo "   https://${DOMAIN}/healthz"
