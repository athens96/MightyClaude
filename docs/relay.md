# 릴레이 연결 (모바일 리모트 v2)

휴대폰과 Mac이 모두 **릴레이 서버에 바깥으로 접속**해 연결된다. 포트 개방·Tailscale·VPN이 필요 없고, 릴레이는 암호문만 넘기는 단순 파이프라서 내용을 볼 수 없다. Paseo(getpaseo/paseo, Apache License 2.0 — 고지는 저장소의 `NOTICE.md`)의 릴레이 구조를 참고했으며 암호 프리미티브는 CryptoKit과 noble 라이브러리에 모두 있는 것으로 골랐다.

## 구성

| 역할 | 구현 | 위치 |
|---|---|---|
| 릴레이 | Node.js + `ws`, 상태 없음, `serverId`로 소켓을 짝지음 | `relay/` (Docker 이미지 포함) |
| 호스트(데몬) | Mac 앱의 `MobileRelayService` | `native/macos/Sources/MightyCore/Remote/` |
| 클라이언트 | Expo 앱의 `relayTransport` | `mobile/src/api/relay/` |

## 릴레이 와이어 (평문, 릴레이가 해석)

WebSocket `GET /ws` + 쿼리. `serverId`는 `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`.

| 소켓 | 쿼리 | 동작 |
|---|---|---|
| 호스트 제어 | `serverId=…&role=server&v=1` | 호스트당 1개. 릴레이가 텍스트 JSON으로 알림: `{"type":"connected","connectionId"}`, `{"type":"disconnected","connectionId"}`. 새 제어 소켓이 오면 이전 제어 소켓은 코드 4409로 닫힌다 |
| 클라이언트 데이터 | `serverId=…&role=client&connectionId=<uuid>&v=1` | 호스트 제어 소켓이 없으면 4404로 즉시 닫음. 있으면 제어 소켓에 `connected`를 보내고 호스트 데이터 소켓을 최대 10초 기다린다(그동안 프레임 64개까지 버퍼, 초과 시 4413). 시간 내 안 오면 4504 |
| 호스트 데이터 | `serverId=…&role=server&connectionId=<uuid>&v=1` | 대기 중인 클라이언트가 없으면 4404. 있으면 두 소켓을 양방향으로 잇는다 |

데이터 소켓의 프레임(텍스트·바이너리)은 그대로 상대에게 전달된다. 한쪽이 닫히면 다른 쪽도 같은 코드로 닫고 제어 소켓에 `disconnected`를 보낸다. 모든 소켓에 30초 간격 WebSocket ping, 프레임 최대 1 MiB, 호스트당 동시 연결 32개. `GET /healthz` → `200 ok`.

## 종단 간 암호화 (릴레이는 해석 불가)

- 호스트 정적 키: X25519. `<데이터 폴더>/mobile-remote/relay-keypair.json`(0600)에 `{v:1, publicKeyB64, secretKeyB64}`.
- 클라이언트: 연결마다 새 X25519 키쌍.
- 핸드셰이크(데이터 소켓의 평문 텍스트 프레임 2개):
  1. 클라이언트 → `{"type":"hello","v":1,"clientKey":"<b64 32B>","nonce":"<b64 16B>"}`
  2. 호스트 → `{"type":"ready","v":1,"serverKey":"<b64 32B>","nonce":"<b64 16B>"}`. 클라이언트는 `serverKey`가 페어링 때 받은 공개키와 같은지 확인한다.
- 키 유도: `shared = X25519(내 비밀키, 상대 공개키)`, `key = HKDF-SHA256(ikm=shared, salt=clientNonce‖serverNonce, info="mightyclaude-relay-v1", 32B)`. 공유 비밀이 모두 0이면 거부.
- 프레임: 바이너리 `[12B nonce][ChaCha20-Poly1305 암호문+16B 태그]`. nonce = `[방향 1B][0,0,0][카운터 8B big-endian]`, 방향은 클라이언트→호스트 0x01, 호스트→클라이언트 0x02. 카운터는 0부터 프레임마다 1씩 증가하고, 받는 쪽은 **직전보다 큰 카운터만** 받아들인다(재전송·순서 뒤바뀜 거부). 평문은 UTF-8 JSON.
- 인증(암호화된 첫 메시지): 클라이언트 → `{"type":"auth","pairingKey":"…","clientName":"…"}`, 호스트 → `{"type":"auth_ok","hostName":"…","hostId":"…","appVersion":"…"}` 또는 `{"type":"auth_error","reason":"pairing-key"}`를 보낸 뒤 소켓을 닫음(클라이언트는 이를 재페어링 필요로 표시). 호스트는 `auth_ok` 전에는 다른 메시지를 처리하지 않는다.

### 기기 토큰 (기기별 해제)

인증 프레임은 선택 필드로 확장된다. 필드를 모르는 구버전 앱·호스트는 지금처럼 동작한다.

- 처음 페어링: 클라이언트 → `{"type":"auth","pairingKey":"…","clientName":"…","clientId":"<b64url 16B, 앱이 한 번 만들어 보안 저장소에 보관>"}`. 호스트는 키가 맞으면 기기를 등록하고 `auth_ok`에 `"deviceToken":"<b64url 32B>"`를 넣어 **한 번만** 돌려준다. 호스트는 토큰의 SHA-256만 저장한다(`<데이터 폴더>/mobile-remote/devices.json`, 0600: `[{ id, name, tokenHash, firstSeen, lastSeen }]`, 최대 32대).
- 이후 접속: 클라이언트 → `{"type":"auth","clientId":"…","deviceToken":"…","clientName":"…"}` (`pairingKey` 없음). 호스트는 해시를 상수 시간으로 비교한다. 등록되지 않았거나 해제된 기기면 `{"type":"auth_error","reason":"device-revoked"}` 후 소켓을 닫고, 클라이언트는 재페어링 필요로 표시한다.
- `clientId` 없이 `pairingKey`만 보내는 구버전 앱은 키가 맞으면 받아들이되 기기 목록에는 "구버전 앱"으로 묶는다(토큰을 발급하지 않는다).
- 해제 순서: **페어링 키를 먼저 새로 만들고**(실패하면 아무것도 바뀌지 않는다) → 기기 항목을 지워 저장하고(저장이 실패하면 해제도 실패로 보고) → 그 기기의 열린 소켓을 닫고 → 그 기기의 업로드를 지운다. 재접속을 반복하는 해제된 휴대폰이 옛 키로 다시 등록할 틈이 없다. 토큰으로 인증한 다른 기기는 키가 바뀌어도 끊기지 않는다. 키로만 인증한 연결(구버전 앱, 아직 토큰을 받지 못한 연결)은 끊긴다.
- 이미 토큰을 가진 `clientId`는 페어링 키만으로 덮어쓸 수 없다(`device-conflict`). 페어링 키를 아는 다른 휴대폰이 남의 기기 항목을 가로채지 못하게 하기 위해서다. 거절당한 앱은 그 호스트 전용 `clientId`를 새로 만들어 한 번 다시 시도한다(앱을 지웠다 다시 깐 경우).
- 기기 목록은 최대 32대다. 가득 차면 90일 넘게 접속하지 않은 항목만 밀어내고, 그런 항목이 없으면 등록을 거절한다(`device-limit`). 새 기기 등록은 호스트 전체에서 1시간에 8대까지다. 등록한 지 24시간이 안 된 기기는 Mac 설정에 "새 기기"로 표시된다.
- 호스트가 목록을 저장하지 못하면 토큰 없이 `auth_ok`를 보낸다. 앱은 이때 페어링 키를 그대로 두고 다음 접속에서 다시 토큰을 받는다.
- Mac 설정의 **구버전 앱 허용**을 끄면 `clientId` 없는 인증을 거절한다(`legacy-refused`). 형식이 틀린 `clientId`는 구버전으로 내려 받지 않고 거절한다(`malformed`).
- `auth_error.reason`: `pairing-key`·`device-revoked`(최종: 앱이 비밀값을 지우고 재페어링 필요로 표시), `device-conflict`(앱이 새 id로 한 번 재시도), `device-limit`·`legacy-refused`·`malformed`·그 밖의 값(최종 아님: 비밀값을 지우지 않고 오류만 보여 준다).
- 기기 토큰은 **관리 기능**이다. 페어링 키(QR)를 가진 사람은 새 기기로 등록할 수 있으므로, QR이 유출됐다면 키를 다시 만들어야 한다.
- 토큰·키·해시는 로그에 남기지 않는다.

## 암호화 채널 위의 메시지

기존 m1 REST 의미를 그대로 터널링한다(라우트·본문은 `docs/mobile-remote.md`).

- 요청: `{"id":"<uuid>","method":"GET"|"POST","path":"/m1/state?since=3&wait=10","body":{…}?}`
- 응답: `{"id":"<같은 id>","status":200,"body":{…}}` (오류도 `status`와 `{protocol, error}` 본문)
- 호스트 발신 알림: `{"type":"notify","scope":"state"|"session:<id>","revision":N}` — 클라이언트는 해당 스코프를 즉시 다시 요청한다. 롱폴 `wait`는 그대로 동작하므로 알림을 놓쳐도 최대 `wait`초 안에 따라잡는다.
- 유지: 20초마다 `{"type":"ping"}` ↔ `{"type":"pong"}`(암호화). 60초 무응답이면 끊고 재접속.
- 동시 요청은 `id`로 구분하며 최대 8개.

## 페어링

QR/문자열: `mightyclaude://pair?v=2&sid=<serverId>&pk=<b64url 공개키>&relay=<wss://host:port 또는 ws://>&key=<pairingKey>&name=<percent-encoded 이름>`.

`pairingKey`는 기존 모바일 리모트 키(32B base64url)를 그대로 쓴다. 키를 다시 만들면 키로만 인증하던 휴대폰(구버전 앱)은 재페어링해야 하고, 기기 토큰을 받은 휴대폰은 그대로 접속한다. 공개키가 바뀌는 일은 없다(키쌍은 파일을 지우지 않는 한 유지).

## 재접속

호스트: 제어 소켓이 끊기면 1초부터 2배씩 늘려 최대 30초 간격으로 재접속. 클라이언트: 1.5초부터 2배씩 최대 30초, 앱이 전면으로 오면 즉시.

## 릴레이 실행

```
cd relay && npm install && npm start            # ws://0.0.0.0:8787
docker build -t mightyclaude-relay relay && docker run -p 8787:8787 mightyclaude-relay
```

공개 인터넷에서는 TLS가 있는 리버스 프록시(Caddy, Cloudflare 등) 뒤에 두고 `wss://`로 쓴다. 릴레이 자체는 어떤 비밀도 알지 못한다.
