# Cloudflare Workers 릴레이

Mighty Code의 기본 릴레이는 Cloudflare Workers에 있습니다: `wss://mighty-relay.athens96.workers.dev` (2026-11-12부터, Mac `MobileWire.defaultRelayURL`). 코드는 `relay/cloudflare/`이고 와이어는 Node 릴레이(`relay/src`, [`relay.md`](relay.md))와 같습니다. 그래서 Mac·Windows 호스트와 휴대폰은 주소만 바꾸면 됩니다. 예전 기본값 `wss://mightyclaude.duckdns.org`(Oracle, [`relay-oracle.md`](relay-oracle.md))는 서버를 내렸고, 그 주소가 설정에 남아 있으면 호스트가 새 기본값을 씁니다(`MobileWire.retiredDefaultRelayURLs`).

## 구성

- **Worker** (`src/worker.ts`): `GET /healthz`, `GET /ws?…`, `/push/v1/*`. 쿼리 검사(4400)와 호스트 토큰 확인(4401)은 Worker가 하고, 소켓은 그 `serverId`의 Hub로 넘깁니다.
- **Hub** (`src/hub.ts`, Durable Object, `serverId`마다 하나): 제어 소켓, 클라이언트 데이터 소켓, 호스트 데이터 소켓을 짝지어 프레임을 그대로 넘깁니다. 닫기 코드(4404 4409 4410 4413 4429 4504)와 `connected`/`disconnected` 알림은 Node 릴레이와 같습니다. 소켓은 hibernation API로 받으므로 오래 열린 제어 소켓이 있어도 메시지가 없는 동안은 요금이 붙지 않습니다. 소켓의 종류와 연결 id는 소켓에 붙여 두고(태그·attachment), 호스트 반쪽이 붙기 전에 클라이언트가 보낸 프레임만 메모리에 둡니다(그동안은 붙기 기다림 타이머가 객체를 깨워 둡니다).
- **PushStore** (`src/push.ts`, Durable Object, 하나): 푸시 핸들을 SQLite 저장소에 두고 FCM HTTP v1로 보냅니다. 전송 규칙은 Node 릴레이와 같은 `relay/src/fcm-core.ts`이고, 서명만 WebCrypto입니다. 한도 창은 메모리에 있어 객체가 내려가면 새로 셉니다.

Node 릴레이와 다른 점:
- **TURN 없음.** 제어 소켓의 `turn-credentials-request`에는 `turn-unavailable`로 답합니다. 화면 공유(BETA)는 호스트에서 잠시 꺼 두었습니다.
- **릴레이가 ping을 보내지 않습니다.** ping 응답은 런타임이 합니다. Mac·Windows 호스트는 제어 소켓에 ping을 보내고, 휴대폰은 암호 채널에서 20초마다 ping합니다.
- **4507이 없습니다.** Workers에는 소켓 송신 버퍼 크기를 잴 방법이 없습니다.
- 클라이언트 IP는 `CF-Connecting-IP`입니다(`RELAY_TRUST_PROXY` 대신).

## 배포

```bash
cd relay/cloudflare
npm install
npx wrangler login                 # 처음 한 번, 브라우저
npx wrangler deploy                # https://mighty-relay.<계정>.workers.dev
# FCM 서비스 계정 키는 비밀값으로. 여러 줄 JSON을 그대로 넣으면 깨지므로 한 줄로 바꿔 넣습니다.
python3 -c 'import json,sys; print(json.dumps(json.load(open(sys.argv[1]))), end="")' key.json | npx wrangler secret put FCM_SERVICE_ACCOUNT
curl https://mighty-relay.<계정>.workers.dev/healthz   # ok
```

키가 맞는지는 가짜 FCM 토큰으로 핸들을 하나 만들어 보내 보면 압니다: `410 token-gone`이면 FCM까지 닿은 것이고, `502`에 `Retry-After: 60`이면 키 교환이 실패한 것, `502`에 `fcmError`가 있으면 그 이름(예: `SENDER_ID_MISMATCH`, 다른 Firebase 프로젝트의 키)이 원인입니다.

한도는 `wrangler.toml`의 `[vars]`나 `--var`로 바꿀 수 있습니다: `RELAY_ATTACH_TIMEOUT_MS` `RELAY_MAX_CONNECTIONS` `RELAY_MAX_BUFFERED_FRAMES` `RELAY_MAX_PAYLOAD`, `PUSH_*`(이름과 기본값은 [`relay/README.md`](../relay/README.md)의 표와 같습니다).

## 시험

```bash
cd relay/cloudflare
npm run typecheck
npm test     # wrangler dev(로컬 workerd)를 띄우고 Node 릴레이 시험과 같은 시나리오를 돌립니다
```

로컬 `wrangler dev`에서는 릴레이가 hibernation 소켓을 닫을 때 클라이언트의 `close` 이벤트가 닫기 프레임보다 10초쯤 늦게 옵니다(TCP를 늦게 끊음). 실제 Cloudflare에서는 바로 옵니다.

## 요금

Workers 무료 요금제 안에서 돕니다: 요청 하루 10만 건, Durable Objects도 무료 요금제에서 쓸 수 있습니다(SQLite 저장소). WebSocket 메시지는 받은 메시지 20개가 요청 1건으로 셉니다. 넘으면 Workers Paid(월 5달러)로 올리면 됩니다.
