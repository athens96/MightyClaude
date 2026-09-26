# Oracle Cloud Always Free 릴레이 배포 가이드

MightyClaude 릴레이를 Oracle Cloud Always Free Ampere A1(arm64) 인스턴스에 배포합니다.  
이 가이드를 끝까지 따르면 `https://<이름>.duckdns.org/healthz` 에서 `ok` 가 출력됩니다.

---

## 1단계 — Oracle Cloud 계정 만들기

1. [oracle.com/cloud/free](https://www.oracle.com/cloud/free/) 에서 **무료로 시작** 을 클릭합니다.
2. 이름·이메일·거주 국가·신용카드(본인 확인용, 자동 청구 없음)를 입력하고 가입을 완료합니다.
3. 로그인 후 우측 상단의 리전 선택기에서 **Japan East** 또는 **South Korea Central** 등 가까운 리전을 선택합니다.  
   (Always Free Ampere A1 인스턴스는 모든 리전에서 지원됩니다.)

## 2단계 — ARM64 VM 인스턴스 만들기

1. 콘솔 검색창에 **Instances** 를 입력하거나 **Compute > Instances** 로 이동합니다.
2. **인스턴스 생성** 을 클릭합니다.
3. **이미지**: Oracle Linux 8 (기본값 유지)
4. **Shape** → **변경** → Ampere 탭 → **VM.Standard.A1.Flex** 선택  
   OCPU: 1, 메모리: 6 GB (Always Free 할당량 내)
5. **SSH 키 추가**: 기존 공개 키(.pub)를 붙여넣거나 **키 페어 생성** 을 눌러 다운로드합니다.  
   비밀 키는 안전한 위치에 보관하세요.
6. 나머지는 기본값으로 두고 **만들기** 를 누릅니다.

> 인스턴스 상태가 **실행 중(Running)** 이 될 때까지 1~2 분 기다립니다.  
> 세부 정보 페이지에서 **공개 IP 주소** 를 메모해 둡니다.

## 3단계 — 클라우드 보안 목록에서 포트 열기

Oracle Cloud는 OS 방화벽 외에 **VCN 보안 목록**도 있습니다.

1. 인스턴스 세부 정보 > **기본 VNIC** > **서브넷** 링크를 클릭합니다.
2. **보안 목록** 탭 > 기본 보안 목록을 엽니다.
3. **인그레스 규칙 추가** 로 아래 두 규칙을 추가합니다.

   | 소스 CIDR | 프로토콜 | 대상 포트 범위 |
   |-----------|----------|----------------|
   | 0.0.0.0/0 | TCP      | 80             |
   | 0.0.0.0/0 | TCP      | 443            |

4. **변경 사항 저장** 을 클릭합니다.

## 4단계 — DuckDNS 서브도메인 및 토큰 받기

1. [duckdns.org](https://www.duckdns.org) 에서 소셜 계정(Google, GitHub 등)으로 로그인합니다.
2. **add domain** 필드에 원하는 이름을 입력하고 **add domain** 을 클릭합니다.  
   예: `myrelay` → 도메인은 `myrelay.duckdns.org` 가 됩니다.
3. 2단계에서 메모한 **공개 IP 주소** 를 Current IP 에 입력하고 **update ip** 를 누릅니다.
4. 페이지 상단의 **token** 값을 복사해 둡니다 (다음 단계에서 `.env` 에 넣습니다).

## 5단계 — 서버에 SSH 접속

```bash
ssh -i /path/to/private_key opc@<공개-IP>
```

Oracle Linux 8 의 기본 사용자 이름은 `opc` 입니다.

## 6단계 — 저장소 클론 및 setup.sh 실행

```bash
# 저장소 클론 (Git 없으면: sudo dnf -y install git)
git clone https://github.com/<owner>/MightyClaude.git
cd MightyClaude/relay/deploy/oracle

# setup.sh 실행 — Docker 설치 + 방화벽 개방 + .env 생성
bash setup.sh
```

처음 실행하면 Docker를 설치한 뒤 `.env` 파일 템플릿을 만들고 종료합니다.  
`newgrp docker` 를 실행해 세션에 docker 그룹을 적용하거나, 재로그인 후 계속합니다.

## 7단계 — .env 설정

```bash
nano .env
```

아래 두 값을 실제 값으로 수정합니다.

```
DUCKDNS_TOKEN=여기에-4단계에서-복사한-토큰-붙여넣기
RELAY_DOMAIN=myrelay.duckdns.org
```

`Ctrl+O` → `Enter` 로 저장하고, `Ctrl+X` 로 nano 를 닫습니다.

## 8단계 — 스택 시작

```bash
bash setup.sh
```

`.env` 파일이 있으므로 이번에는 컨테이너를 빌드하고 시작합니다.  
**첫 실행**은 Caddy 이미지를 xcaddy 로 빌드하므로 5~10 분 정도 걸립니다.

## 9단계 — 동작 확인

```bash
curl https://myrelay.duckdns.org/healthz
```

`ok` 가 출력되면 배포가 완료된 것입니다. 🎉  
브라우저에서 `https://<이름>.duckdns.org/healthz` 를 열어 확인해도 됩니다.

## 10단계 — Mac 앱에 릴레이 주소 입력

1. MightyClaude Mac 앱 → **설정 > 모바일 리모트** 를 엽니다.
2. **릴레이 주소** 필드에 `wss://myrelay.duckdns.org` 를 입력합니다.
3. QR 코드가 표시되면 휴대폰 앱으로 스캔하여 페어링합니다.

---

## 문제 해결

| 증상 | 확인 사항 |
|------|-----------|
| `curl` 타임아웃 | 3단계의 Oracle Cloud 보안 목록, OS 방화벽: `sudo firewall-cmd --list-ports` |
| SSL 인증서 오류 | `docker compose logs caddy` 로 ACME 오류 확인. DUCKDNS_TOKEN 값과 DuckDNS IP 등록 여부 확인 |
| `healthz` 가 502 | `docker compose logs relay` 로 릴레이 컨테이너 상태 확인 |
| 재부팅 후 서비스 중단 | `docker compose ps` — `restart: unless-stopped` 정책으로 자동 재시작됩니다 |
| Docker 명령 권한 오류 | `newgrp docker` 또는 재로그인 후 재시도 |

## 업데이트

```bash
cd MightyClaude/relay/deploy/oracle
git pull
docker compose up -d --build
```
