# Oracle Cloud Always Free에 릴레이 올리기

MightyClaude 릴레이(`relay/`)를 Oracle Cloud Always Free 인스턴스에 올려, 휴대폰이 어느 네트워크에서든 Mac에 닿게 합니다. 끝나면 `https://<이름>.duckdns.org/healthz`가 `ok`를 돌려주고, Mac에 넣을 주소는 `wss://<이름>.duckdns.org`입니다.

릴레이는 암호문만 전달하고 아무 비밀도 모릅니다(docs/relay.md). 서버에 둘 비밀값도 없습니다 — 인증서는 Caddy가 80·443 포트로 직접 받습니다.

## 0. 시작 전에

- **홈 리전**: 무료 인스턴스는 가입할 때 고른 홈 리전에만 만들 수 있고 홈 리전은 바꿀 수 없습니다. 한국에서 쓰기에는 싱가포르(`ap-singapore-1`)나 도쿄·오사카도 충분히 빠릅니다(릴레이는 짧은 암호문만 오갑니다).
- **유휴 회수 막기(권장)**: 무료 계정의 인스턴스는 7일 동안 CPU·네트워크·메모리 사용이 아주 낮으면 Oracle이 회수할 수 있고, 릴레이는 대부분의 시간에 거의 놉니다. 콘솔의 **청구 및 비용 관리 → 결제 방법/업그레이드**에서 **Pay As You Go**로 올리면 회수 대상에서 빠지고, Always Free 한도 안에서는 여전히 0원입니다. 올린 뒤 **예산(Budgets)**에 1달러 알림을 걸어 두세요.

## CLI로 한 번에 (권장)

아래 1~5단계를 Mac에서 명령 하나로 대신합니다. 먼저 [duckdns.org](https://www.duckdns.org)에서 이름을 하나 등록해 두세요(예: `mighty-young-relay` → `mighty-young-relay.duckdns.org`). 이름은 먼저 등록한 사람의 것이라 남의 이름이나 등록하지 않은 이름으로는 인증서를 받지 못합니다.

```bash
brew install oci-cli
oci session authenticate --region ap-singapore-1 --profile-name mighty   # 브라우저로 로그인. 리전은 가입한 홈 리전
git clone --depth 1 https://github.com/athens96/MightyClaude.git && cd MightyClaude
bash relay/deploy/oracle/provision.sh --profile mighty --domain mighty-young-relay.duckdns.org
```

`provision.sh`가 하는 일:
- 전용 VCN·인터넷 게이트웨이·보안 규칙(22/80/443)·서브넷을 만듭니다.
- Ubuntu 24.04 인스턴스를 A1 1 OCPU/6 GB로 만들고, 용량이 없으면 E2.1.Micro로 다시 시도합니다.
- 예약 공인 IP를 붙인 뒤, 그 IP를 보여 주고 DuckDNS에 넣을 때까지 기다립니다.
- 새 인스턴스는 첫 부팅 때(cloud-init) 스스로 저장소를 받아 `setup.sh`를 돌리므로 SSH가 필요 없습니다 — 22번 포트를 막는 회사·공용 네트워크에서도 됩니다. 스크립트는 `https://<이름>/healthz`가 `ok`가 될 때까지 기다렸다가 `wss://…` 주소를 알려 줍니다. (이 기능 전에 만든 인스턴스는 SSH로 설정하므로 22번이 열린 네트워크가 필요합니다.)

같은 이름의 자원은 다시 만들지 않으니 중간에 멈춰도 다시 돌리면 됩니다. SSH 키는 `~/.ssh/mightyclaude-relay`에 새로 만들어지고, 로그인 세션(약 1시간)이 끝났다면 `oci session authenticate`를 다시 하면 됩니다. Pay As You Go 업그레이드와 DuckDNS의 IP 입력만 웹에서 합니다.

아래는 같은 일을 콘솔에서 손으로 하는 순서입니다.

## 1. 인스턴스 만들기

1. 콘솔에서 **Compute → Instances → Create instance**.
2. **Image**: **Canonical Ubuntu 24.04**를 고릅니다(ARM이면 aarch64 판이 자동으로 골라집니다).
3. **Shape**: **Ampere → VM.Standard.A1.Flex**, OCPU 1 · 메모리 6 GB.
   - "Out of capacity(용량 부족)"가 나오면 잠시 뒤 다시 시도하거나, 다른 가용성 도메인을 고르거나, **Specialty and previous generation → VM.Standard.E2.1.Micro**(AMD, 역시 Always Free)로 만드세요. 릴레이는 1GB 메모리로도 충분하고, 이 키트는 두 종류 모두에서 그대로 동작합니다.
4. **Networking**: 기본 VCN과 공인 서브넷, **Assign a public IPv4 address** 켜짐.
5. **Add SSH keys**: **Generate a key pair for me**를 눌러 개인 키를 내려받거나, 가진 공개 키(`~/.ssh/id_ed25519.pub`)를 붙여 넣습니다.
6. **Create**. 상태가 **Running**이 되면 상세 화면의 **Public IP address**를 적어 둡니다.

## 2. 공인 IP 고정(권장)

인스턴스를 멈췄다 켜도 주소가 바뀌지 않게, 1개 무료인 예약 IP로 바꿉니다.

**Instance → Attached VNICs → (VNIC) → IPv4 Addresses → (…) Edit → Reserved public IP → Create new reserved IP**. 새 주소를 다시 적어 둡니다.

## 3. DuckDNS 이름 만들기

1. [duckdns.org](https://www.duckdns.org)에 소셜 계정으로 로그인합니다.
2. 원하는 이름을 넣고 **add domain** (예: `myrelay` → `myrelay.duckdns.org`).
3. **current ip**에 2단계의 공인 IP를 넣고 **update ip**.

토큰은 필요 없습니다(예약 IP라 주소가 바뀌지 않으므로).

## 4. Oracle 보안 목록에서 80·443 열기

**Instance → Primary VNIC의 Subnet → Security Lists → Default Security List → Add Ingress Rules**:

| Source CIDR | IP Protocol | Destination Port Range |
|---|---|---|
| 0.0.0.0/0 | TCP | 80 |
| 0.0.0.0/0 | TCP | 443 |

(OS 안의 방화벽은 다음 단계의 스크립트가 엽니다.)

## 5. 서버에서 한 줄 실행

Mac 터미널에서 접속합니다(Ubuntu 이미지의 사용자는 `ubuntu`, Oracle Linux는 `opc`):

```bash
chmod 600 ~/Downloads/ssh-key-*.key          # 콘솔에서 받은 개인 키라면
ssh -i ~/Downloads/ssh-key-XXXX.key ubuntu@<공인-IP>
```

서버에서:

```bash
git clone --depth 1 https://github.com/athens96/MightyClaude.git
bash MightyClaude/relay/deploy/oracle/setup.sh myrelay.duckdns.org
```

스크립트가 하는 일: Docker 설치 → OS 방화벽에서 80·443 개방 → 릴레이 이미지 빌드와 Caddy 시작 → 인증서 발급을 기다렸다가 `https://myrelay.duckdns.org/healthz`가 `ok`인지 확인. 첫 실행은 몇 분 걸립니다. 마지막에 `✔ 완료. Mac의 릴레이 주소: wss://myrelay.duckdns.org`가 나오면 끝입니다.

## 6. Mac에 넣기

MightyClaude → **설정 → 모바일 리모트 → 릴레이**에 `wss://myrelay.duckdns.org`를 넣고 **적용**. QR이 뜨면 휴대폰으로 스캔합니다. 앱에 기본값으로 박아 넣고 싶다면 이 주소를 알려 주세요 — `MobileWire.defaultRelayURL` 한 줄입니다.

## 문제 해결

| 증상 | 확인 |
|---|---|
| 스크립트 끝에서 `✘ … ok를 돌려주지 않습니다` | DuckDNS의 IP가 공인 IP와 같은지, 4단계 보안 목록, `sudo docker compose -f ~/MightyClaude/relay/deploy/oracle/compose.yaml logs caddy` |
| `curl`이 멈춤(타임아웃) | 4단계 보안 목록. Ubuntu라면 `sudo iptables -L INPUT -n --line-numbers`에서 80·443 ACCEPT가 REJECT보다 위에 있는지 |
| 인증서 오류(`acme`) | 80 포트가 밖에서 열려 있어야 Let's Encrypt가 확인합니다. 같은 이름으로 너무 자주 재발급하면 한도에 걸리니 한 시간쯤 기다렸다 다시 |
| `healthz`가 502 | `sudo docker compose … logs relay` |
| 재부팅 뒤 | 두 컨테이너는 `restart: unless-stopped`라 저절로 다시 뜹니다 |

## 업데이트

```bash
cd ~/MightyClaude && git pull
bash relay/deploy/oracle/setup.sh
```

(두 번째부터는 이름을 생략하면 `.env`에 적힌 이름을 씁니다.)
