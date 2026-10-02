# 릴레이 연결 (모바일 리모트 v2)

휴대폰과 Mac이 모두 **릴레이 서버에 바깥으로 접속**해 연결된다. 포트 개방이나 VPN이 필요 없고, 릴레이는 암호문만 넘기는 단순 파이프라서 내용을 볼 수 없다. Paseo(getpaseo/paseo, Apache License 2.0 — 고지는 저장소의 `NOTICE.md`)의 릴레이 구조를 참고했으며 암호 프리미티브는 CryptoKit과 noble 라이브러리에 모두 있는 것으로 골랐다.

## 구성

| 역할 | 구현 | 위치 |
|---|---|---|
| 릴레이 | Node.js + `ws`, 상태 없음, `serverId`로 소켓을 짝지음 | `relay/` (Docker 이미지 포함) |
| 호스트(데몬) | Mac 앱의 `MobileRelayService` | `native/macos/Sources/MightyCore/Remote/` |
| 클라이언트 | Expo 앱의 `relayTransport` | `mobile/src/api/relay/` |

## 릴레이 와이어 (평문, 릴레이가 해석)

WebSocket `GET /ws` + 쿼리. `serverId`는 `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$`.

호스트 토큰: 호스트는 무작위 32바이트를 16진수 64자로 적은 `hostToken`을 `<데이터 폴더>/mobile-remote/relay-host-token.json`(0600)에 한 번 만들어 두고, 자기 `serverId`를 그 토큰에서 만든다: `serverId = hex(SHA-256(hostToken의 UTF-8 바이트))`(소문자 64자). 호스트 소켓(제어·데이터)은 모두 `hostToken`(`^[A-Fa-f0-9]{32,64}$`, 형식이 틀리면 4400)을 싣고, 릴레이는 연결마다 `SHA-256(hostToken)`이 `serverId`와 같은지 다시 계산해 다르거나 토큰이 없으면 4401로 닫는다. 릴레이는 토큰을 기억하지 않으므로 호스트가 떨어져 있든 릴레이가 재시작했든 확인 결과가 같다. 클라이언트 데이터 소켓은 토큰을 싣지 않는다.

| 소켓 | 쿼리 | 동작 |
|---|---|---|
| 호스트 제어 | `serverId=…&role=server&v=1&hostToken=…` | 호스트당 1개. 릴레이가 텍스트 JSON으로 알림: `{"type":"connected","connectionId"}`, `{"type":"disconnected","connectionId"}`. 새 제어 소켓이 오면 이전 제어 소켓은 코드 4409로 닫힌다 |
| 클라이언트 데이터 | `serverId=…&role=client&connectionId=<uuid>&v=1` | 호스트 제어 소켓이 없으면 4404로 즉시 닫음. 있으면 제어 소켓에 `connected`를 보내고 호스트 데이터 소켓을 최대 10초 기다린다(그동안 프레임 64개까지 버퍼, 초과 시 4413). 시간 내 안 오면 4504 |
| 호스트 데이터 | `serverId=…&role=server&connectionId=<uuid>&v=1&hostToken=…` | 대기 중인 클라이언트가 없으면 4404. 있으면 두 소켓을 양방향으로 잇는다 |

데이터 소켓의 프레임(텍스트·바이너리)은 그대로 상대에게 전달된다. 한쪽이 닫히면 다른 쪽도 같은 코드로 닫고 제어 소켓에 `disconnected`를 보낸다. `GET /healthz` → `200 ok`.

## 릴레이 제한 및 닫기 코드

| 제한 | 기본값 | 환경 변수 | 닫기 코드 | 설명 |
|---|---|---|---|---|
| 프레임 최대 크기 | 1 MiB | `RELAY_MAX_PAYLOAD` | 1009 (ws 라이브러리) | 초과 시 연결 즉시 종료 |
| 호스트당 동시 연결 | 32개 | `RELAY_MAX_CONNECTIONS` | 4429 | 33번째 클라이언트 거절 |
| 호스트 대기 중 프레임 버퍼 | 64개 | `RELAY_MAX_BUFFERED_FRAMES` | 4413 | 호스트 연결 전 클라이언트 초과 시 |
| 소켓당 송신 버퍼 | 4 MiB | `RELAY_MAX_SOCKET_BUFFERED_BYTES` | 4507 | 전달이 한도를 초과하면 해당 연결만 종료 (제어 소켓·다른 연결 무관) |
| 호스트 데이터 소켓 대기 시간 | 10초 | `RELAY_ATTACH_TIMEOUT_MS` | 4504 | 시간 내 호스트 미연결 시 |
| ping 간격 | 30초 | `RELAY_PING_INTERVAL_MS` | — | 응답 없으면 소켓 강제 종료 |

전체 닫기 코드 목록:

| 코드 | 이름 | 발생 조건 |
|---|---|---|
| 1000 | normal | 정상 종료 |
| 4400 | badRequest | 쿼리 파라미터 오류 |
| 4401 | unauthorized | 호스트 소켓의 `hostToken`이 없거나 `SHA-256(hostToken)`이 `serverId`와 다름 |
| 4404 | notFound | 호스트 제어 소켓 없음, 또는 알 수 없는 connectionId |
| 4409 | conflict | 새 제어 소켓으로 대체됨 |
| 4410 | hostOffline | 호스트 제어 소켓 연결 끊김 |
| 4413 | bufferOverflow | 호스트 연결 전 클라이언트 프레임 초과 |
| 4429 | tooManyConnections | serverId당 연결 수 초과 |
| 4504 | attachTimeout | 호스트 데이터 소켓 시간 초과 |
| 4507 | socketBufferOverflow | 소켓 송신 버퍼 한도 초과 — 해당 연결만 종료 |

## 종단 간 암호화 (릴레이는 해석 불가)

- 호스트 정적 키: X25519. `<데이터 폴더>/mobile-remote/relay-keypair.json`(0600)에 `{v:1, publicKeyB64, secretKeyB64}`.
- 클라이언트: 연결마다 새 X25519 키쌍.
- 핸드셰이크(데이터 소켓의 평문 텍스트 프레임 2개):
  1. 클라이언트 → `{"type":"hello","v":1,"clientKey":"<b64 32B>","nonce":"<b64 16B>"}`
  2. 호스트 → `{"type":"ready","v":1,"serverKey":"<b64 32B>","nonce":"<b64 16B>"}`. 클라이언트는 `serverKey`가 페어링 때 받은 공개키와 같은지 확인한다.
- 키 유도: `shared = X25519(내 비밀키, 상대 공개키)`, `key = HKDF-SHA256(ikm=shared, salt=clientNonce‖serverNonce, info="mightyclaude-relay-v1", 32B)`. 공유 비밀이 모두 0이면 거부.
- 프레임: 바이너리 `[12B nonce][ChaCha20-Poly1305 암호문+16B 태그]`. nonce = `[방향 1B][0,0,0][카운터 8B big-endian]`, 방향은 클라이언트→호스트 0x01, 호스트→클라이언트 0x02. 카운터는 0부터 프레임마다 1씩 증가하고, 받는 쪽은 **직전보다 큰 카운터만** 받아들인다(재전송·순서 뒤바뀜 거부). 평문은 UTF-8 JSON.
- 인증(암호화된 첫 메시지): 클라이언트 → `{"type":"auth","pairingKey":"…","clientName":"…"}`, 호스트 → `{"type":"auth_ok","hostName":"…","hostId":"…","appVersion":"…","capabilities":["…"]}`(`capabilities`는 `GET /m1/info`와 같은 목록) 또는 `{"type":"auth_error","reason":"pairing-key"}`를 보낸 뒤 소켓을 닫음(클라이언트는 이를 재페어링 필요로 표시). 호스트는 `auth_ok` 전에는 다른 메시지를 처리하지 않는다.

### 기기 토큰과 연결 키 교체

인증 프레임은 선택 필드로 확장된다. 필드를 모르는 구버전 앱·호스트는 지금처럼 동작한다.

- 처음 페어링: 클라이언트 → `{"type":"auth","pairingKey":"…","clientName":"…","clientId":"<b64url 16B, 앱이 한 번 만들어 보안 저장소에 보관>"}`. 호스트는 키가 맞으면 기기를 등록하고 `auth_ok`에 `"deviceToken":"<b64url 32B>"`를 넣어 **한 번만** 돌려준다. 호스트는 토큰의 SHA-256만 저장한다(`<데이터 폴더>/mobile-remote/devices.json`, 0600: `[{ id, name, tokenHash, firstSeen, lastSeen }]`, 최대 32대).
- 이후 접속: 클라이언트 → `{"type":"auth","clientId":"…","deviceToken":"…","clientName":"…"}` (`pairingKey` 없음). 호스트는 해시를 상수 시간으로 비교한다. 등록되지 않았거나 해제된 기기면 `{"type":"auth_error","reason":"device-revoked"}` 후 소켓을 닫고, 클라이언트는 재페어링 필요로 표시한다.
- `clientId` 없이 `pairingKey`만 보내는 구버전 앱은 키가 맞으면 받아들이되 기기 목록에는 "구버전 앱"으로 묶는다(토큰을 발급하지 않는다).
- 기기 해제 또는 **키 다시 만들기**는 페어링 키를 교체하고 기기 목록 전체를 비우는 작업이다. 정상적으로 목록을 비운 뒤에는 토큰으로 인증했던 연결도 종료되며, 옛 QR과 기존 기기 토큰은 다시 사용할 수 없다. 모든 휴대폰이 새 QR로 다시 페어링해야 한다. 해제 대상으로 선택한 기기의 업로드도 지운다.
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

`sid`는 위의 호스트 토큰에서 만든 `serverId`다. 토큰에서 만들기 전의 `serverId`로 페어링한 휴대폰은 한 번 다시 페어링해야 한다.

`pairingKey`는 기존 모바일 리모트 키(32B base64url)를 그대로 쓴다. 키를 다시 만들면 키로만 인증하던 휴대폰(구버전 앱)은 재페어링해야 하고, 기기 토큰을 받은 휴대폰은 그대로 접속한다. 공개키가 바뀌는 일은 없다(키쌍은 파일을 지우지 않는 한 유지).

## 재접속

호스트: 제어 소켓이 끊기면 1초부터 2배씩 늘려 최대 30초 간격으로 재접속. 클라이언트: 1.5초부터 2배씩 최대 30초, 앱이 전면으로 오면 즉시.

호스트가 제어 소켓이 죽은 것을 스스로 알아차리는 길:

- **ping**: 연결된 동안 20초마다 WebSocket ping을 보내고 10초 안에 pong이 없으면 소켓을 닫고 위의 재접속으로 넘어간다. 소켓이 죽은 뒤 알아차리기까지 최악 20 + 10 = 30초. 네트워크가 바뀌면 Mac 쪽 소켓은 오류 없이 반쯤 열린 채 남고, 릴레이는 ping 무응답으로 이미 호스트를 내보냈으므로 휴대폰에는 호스트가 오프라인으로 보인다. 상태 문구는 "릴레이가 응답하지 않아 연결을 다시 엽니다." 릴레이가 보내는 ping은 WebSocket 제어 프레임이라 URLSession이 스스로 pong으로 답하고 앱에는 전달되지 않으므로, 호스트가 이를 보고 알아차릴 수는 없다.
- **네트워크 변화**(`NWPathMonitor`, 모바일 리모트가 켜져 있는 동안만): 연결에 쓰는 인터페이스·게이트웨이가 바뀌거나 네트워크가 돌아오면 연결됨으로 보여도 새로 접속한다. 보고가 1.5초 조용해진 뒤 한 번만 판단하므로 접속은 변화가 가라앉고 약 1.5초 뒤에 일어나며, 보고가 끊이지 않아도 첫 보고 뒤 5초 안에는 판단한다. 그 사이의 보고는 모두 기억한다: 같은 192.168.x.1 공유기 사이의 와이파이 전환처럼 "와이파이 → 없음 → 와이파이"로 끝이 처음과 같아도, 중간에 다른 경로나 끊김이 있었으면 새로 접속한다. 네트워크가 흔들려 계속 바뀌면 경로 변화로 인한 재접속은 최소 5초 간격을 두고, 잇따르면 간격을 2배씩 늘려 최대 30초까지 벌린다. 네트워크가 아예 없으면 재시도하지 않고 "네트워크에 연결되어 있지 않습니다."를 보여 준다. 연결이 필요하다고만 보고되는 경로(`.requiresConnection`, 필요할 때 켜지는 VPN)는 연결된 것으로 친다. 접속 시도가 그런 VPN을 켜기 때문이다.
- **잠자기에서 깨어남**(`NSWorkspace.didWakeNotification`): 같은 1.5초 창을 거쳐 새로 접속한다. 깨어나며 네트워크도 바뀌면 한 번만 접속한다. 간격 제한은 받지 않지만 네트워크가 없으면 기다린다.
- **설정의 "다시 연결"**: 연결됨으로 보여도, 네트워크가 없다고 보고된 때에도 언제나 바로 새로 접속한다.

새 제어 소켓이 붙으면 릴레이는 같은 serverId의 옛 소켓을 4409로 닫으므로 언제 새로 접속해도 안전하다. 휴대폰의 호스트 화면은 `host-offline`(4404·4410)을 받아도 위의 백오프로 스스로 다시 붙고, 호스트 목록은 오프라인으로 보이는 호스트를 호스트마다 따로 1.5초부터 2배씩 최대 30초 간격으로 다시 확인한다. 다시 확인하는 동안에도 목록은 "확인 중"으로 바뀌지 않고 직전 상태를 그대로 보여 주며, 오프라인인 호스트가 없으면 타이머를 돌리지 않는다.

## TURN 자격증명 발급 (릴레이 평문, E2EE 아님)

릴레이는 coturn `use-auth-secret`을 환경 변수 `TURN_SECRET`으로 보관한다. 클라이언트·호스트 앱에는 절대 전달되지 않는다.

**요청** — 인증된 호스트 제어 소켓이 평문 텍스트 프레임으로 전송:

```json
{ "type": "turn-credentials-request" }
```

**응답** (제어 소켓에만, 클라이언트 데이터 소켓에는 절대 전달 안 됨):

| type | 설명 |
|---|---|
| `turn-credentials` | `username`, `password`(HMAC-SHA1), `ttl`(초), `uris` 포함. 호스트가 이를 E2EE 채널의 `iceServers`로 감싸 휴대폰에 전달한다(아래 화면 공유 시그널링) |
| `turn-rate-limited` | `retryAfterSecs` 포함. 창(기본 10분)당 serverId별 5회, IP별 10회, 릴레이 전체 60회 중 하나라도 넘으면 |
| `turn-unavailable` | `TURN_SECRET`이 설정되지 않은 경우 |

HMAC-SHA1 형식 (coturn REST API):
- `username = "<만료_유닉스초>:<serverId 앞 16자>"`
- `password = base64(HMAC-SHA1(TURN_SECRET, username))`

자격증명은 호스트만 요청할 수 있다(클라이언트 데이터 소켓에는 `turn-credentials-request`가 먹히지 않고, 응답도 제어 소켓에만 간다). 휴대폰은 Mac이 E2EE로 전달해 준 것만 본다. 호스트는 만료 전에 다시 요청해 `screen-grant`로 새 자격증명을 보내고 ICE restart를 건다.

**"호스트만"의 실제 의미.** 호스트 인증은 스스로 증명하는 것이다: `serverId = SHA-256(hostToken)`이고 `hostToken`은 호스트가 혼자 고른 무작위 값이다. 그래서 이 확인은 "다른 호스트의 serverId를 빼앗지 못한다"는 것만 보장하고, "등록된 Mac만 TURN을 쓴다"는 것은 보장하지 않는다 — 누구든 새 토큰을 만들어 새 serverId로 자격증명을 받을 수 있다. 남용을 실제로 막는 것은 아래 두 가지다:

- 릴레이의 발급 한도: serverId별·IP별·릴레이 전체(`TURN_MAX_GLOBAL`) 창당 횟수. 새 serverId를 무한히 만들어도 전체 한도에서 멈춘다. 릴레이가 리버스 프록시 뒤에 있으면 `RELAY_TRUST_PROXY=1`로 IP별 한도가 프록시가 아니라 실제 클라이언트 IP(프록시가 붙인 `X-Forwarded-For`의 마지막 값)에 걸리게 한다. 이 값이 IP가 아니면 소켓 주소를 쓴다. 프록시 없이 이 플래그를 켜면 클라이언트가 헤더로 IP를 꾸밀 수 있으니 켜지 않는다.
- coturn의 할당량: 세션당 2 Mbps(`max-bps=250000`), 전체 8 Mbps(`bps-capacity=1000000`) — coturn은 이 둘을 초당 **바이트**로 센다. 사용자당 4개·전체 8개 할당, 사설·루프백·링크로컬·메타데이터·멀티캐스트·문서용 대역과 IPv6 루프백·ULA·링크로컬·IPv4 매핑·NAT64 대역으로의 중계는 거절된다.

환경 변수:

| 변수 | 기본값 | 설명 |
|---|---|---|
| `TURN_SECRET` | (없음) | coturn use-auth-secret. 비어 있으면 TURN 비활성화 |
| `TURN_HOST` | `127.0.0.1` | TURN 서버 주소 (URI에 사용) |
| `TURN_PORT` | `3478` | TURN 서버 포트 |
| `TURN_CREDENTIAL_TTL_SECS` | `3600` | 자격증명 유효 시간 |
| `TURN_RATE_WINDOW_MS` | `600000` | 발급 속도 제한 창 (ms) |
| `TURN_MAX_PER_SERVER_ID` | `5` | 창당 serverId별 최대 발급 횟수 |
| `TURN_MAX_PER_IP` | `10` | 창당 IP별 최대 발급 횟수 |
| `TURN_MAX_GLOBAL` | `60` | 창당 릴레이 전체 최대 발급 횟수 |
| `RELAY_TRUST_PROXY` | (꺼짐) | `1`이면 IP별 한도에 `X-Forwarded-For`의 마지막 값을 쓴다. 릴레이 앞의 프록시가 그 값을 붙일 때만 켠다(Oracle 배포의 Caddy) |

## 화면 공유 시그널링 (E2EE, 릴레이 해석 불가)

BETA 기능인 **화면 보기·조작**의 WebRTC 시그널링은 위의 암호화 채널 위에 JSON으로 흐른다. 릴레이는 이 프레임을 바이너리 그대로 넘기므로 SDP·ICE 후보·권한·킬 신호 중 어느 것도 읽거나 바꿀 수 없다(X25519/HKDF/ChaCha20-Poly1305 + 엄격한 카운터). 영상은 릴레이를 지나지 않는다: 같은 와이파이에서는 ICE **host** 후보로 바로, 그 밖에서는 **srflx·prflx**로 P2P, 그마저 막히면 coturn TURN(**relay** 후보)으로만 우회한다.

사용자가 Mac과 안드로이드 휴대폰으로 직접 따라 하는 기기 확인표(와이파이·LTE·강제 TURN 경로, 측정, 입력, 안전)는 [`docs/screen-share-device-checklist.md`](screen-share-device-checklist.md)에 있다.

무엇이 어디로 흐르는지:

| 내용 | 경로 |
|---|---|
| offer·answer·ICE 후보, 세션 종료, 권한, 킬 | 릴레이의 E2EE 채널 (이 절의 메시지 타입) |
| 영상 프레임 | WebRTC 피어 연결 (host → srflx/prflx → 최후에 TURN relay) |
| 입력 이벤트(포인터·스크롤·키·확정된 문자열), 클립보드(양방향·수동 버튼·최대 1 MB·zstd) | 피어 연결의 데이터 채널. 릴레이를 지나지 않으므로 릴레이 프레임 한도와 무관하다 |
| getStats 지표(RTT·지터·디코드 시간·프리즈·비트레이트, 선택된 후보 종류 `host`/`srflx`/`prflx`/`relay`) | 휴대폰 안에서만 집계·표시. 릴레이로 보내지 않는다 |

### 호스트 기능 광고 (`screenShare`)

기능 광고는 기존 방식을 그대로 쓴다. 호스트는 `GET /m1/info`의 `capabilities` 배열(`docs/mobile-remote.md`의 `MobileInfo.capabilities`)과 `auth_ok`의 `capabilities`에 이름 하나를 더한다: **`"screenShare"`**. 화면 공유 엔진(캡처·WebRTC)이 붙은 앱에서만 넣는다 — 엔진이 없으면 아래 경로가 503으로 답하므로 이름도 빠진다.

```json
{ "protocol": 1, "hostId": "…", "hostName": "…", "appVersion": "…", "platform": "macOS",
  "capabilities": ["queue", "pane", "files", "screenShare"] }
```

- 배열이 없거나 `"screenShare"`가 없으면 휴대폰은 화면 공유 화면과 진입점을 **숨긴다**(구버전 Mac).
- 휴대폰은 자기가 아는 이름만 남기고 나머지는 버리므로, `screenShare`를 모르는 구버전 앱은 이름을 무시하고 기능을 띄우지 않는다. 모르는 `type`의 암호화 메시지도 양쪽 모두 조용히 버리므로, 어느 쪽이 구버전이어도 연결이 깨지지 않는다.
- `clientId` 없이 인증한 구버전 앱(기기 목록에서 "구버전 앱"으로 묶이는 쪽)은 기기를 서로 구분할 수 없어 허용 목록에 올릴 수 없다. 세션 요청은 `legacy-client` 이유로 거절된다.
- 기능 자체에는 전역 끄기 스위치가 없다. 그래도 휴대폰마다 허용 목록(`screenShareAllowed`)과 보기/조작 권한(`grant`)이 따로 필요하고, 판단은 전부 Mac이 한다. 새로 페어링한 휴대폰은 허용 목록에서 꺼진 상태(`allowed:false`, `grant:"none"`)로 시작한다.

### 세션 요청 (기존 m1 터널)

세션을 **시작**하는 쪽은 휴대폰이고, 새 푸시 타입을 쓰지 않고 위의 요청/응답 터널을 그대로 쓴다(`{"id":…,"method":…,"path":…}`). 본문 규약은 이 문서가 기준이다.

- `GET /m1/screen-share/state` → `{"screenShare":{"allowed":bool,"grant":"none"|"view"|"control","isBeta":true,"displays":[{"displayId":N,"width":N,"height":N,"main":bool}],"controlChallengeB64":"…","controlKeyFingerprint":"XXXX-XXXX-XXXX-XXXX","iceServers":[…],"idleTimeoutSeconds":600,"tapMarker":true}}`
  - `controlChallengeB64`는 한 번만 쓰는 바이트열(`screen-control-challenge:<id>:<무작위>`)이며 허용 목록에 있고 `grant`가 `control`일 때만 들어 있다. 상태를 읽을 때마다 새로 만들어지고, 2분 안에 한 번의 조작 세션 시작에만 쓰인다(성공하든 실패하든 그 시도로 소진된다).
  - `controlKeyFingerprint`는 Mac이 이 휴대폰의 조작 키를 저장해 두었을 때만 들어 있는 짧은 지문(공개키 SHA-256 앞 8바이트, 4글자씩 `-`로 묶은 16진수)이다. 비밀이 아니다. 휴대폰은 이 값으로 "키를 등록해야 하는가"를 판단하므로, 화면을 열 때 생체 인증을 띄울 필요가 없다. 값이 없는데 휴대폰에 키가 있으면 Mac이 키를 잊은 것이므로 다시 등록한다.
  - `iceServers`는 릴레이가 발급한 짧은 수명(기본 1시간)의 TURN 자격증명이다(위 [TURN 자격증명 발급](#turn-자격증명-발급-릴레이-평문-e2ee-아님)). coturn 비밀값은 릴레이에만 있고 Mac·휴대폰에는 절대 오지 않는다.
  - `idleTimeoutSeconds`는 조작 600초, 보기 전용 1800초다.
  - `tapMarker`는 Mac이 아래 [탭 표식](#탭-표식과-측정-장면)을 그릴 수 있을 때 `true`다. 없거나 `false`(구버전 Mac)면 휴대폰은 표식 없이 재는 예전 방식으로 돌아간다.
- `POST /m1/screen-share/sessions` 본문 `{"mode":"view"|"control","displayId":N,"controlSignatureB64":"…","network":"wifi"|"cellular","decodes":["H264","VP9","AV1"]}` → `200 {"sessionId":"…","mode":"…","displayId":N,"codec":"H264"|"VP9"|"AV1","quality":{"width":N,"height":N,"fps":N,"maxBitrateKbps":N}}`
  - `mode:"control"`은 매 세션 `controlSignatureB64`가 필요하다. 생체·PIN이 걸린 Android Keystore 키(P-256)로 `controlChallengeB64`에 서명한 ECDSA DER 서명이고, 권한을 줄 때 등록해 둔 공개키(`controlKeyPublic`)로 **Mac이** 검증한다. 휴대폰 쪽 확인만으로는 조작 세션이 열리지 않는다.
  - `decodes`는 휴대폰이 `getCapabilities`로 확인한 디코딩 가능 코덱이다. 기본값은 하드웨어 H.264이고, 모바일 데이터에서 Mac CPU에 여유가 있고 휴대폰이 디코딩할 수 있을 때만 VP9/AV1을 고른다. CPU·발열 압박이 생기면 H.264로 되돌린다. HEVC는 쓰지 않는다.
  - `quality`는 네트워크별 상한이다: 와이파이 최대 1080p30(약 6 Mbps), 모바일 데이터 최대 720p15(약 1 Mbps, 5–15 fps). TURN relay 경로에서는 세션 대역 할당량(기본 2 Mbps)까지로 더 낮춘다. 화면이 멈춰 있으면 프레임을 보내지 않아 유휴 트래픽은 0에 가깝다.
  - 거절은 `403 {"error":{"reason":"…"}}`: `legacy-client`, `device-not-allowed`(허용 목록 밖 — 새로 페어링한 휴대폰의 기본값), `insufficient-grant`, `control-signature`, `concurrency-limit`(조작 1대 + 보기 전용 2대까지), `screen-permission`(Mac의 화면 기록 권한이 없거나 만료됨 — 휴대폰은 "Mac에서 승인 필요"를 보여 준다), `lock-screen`·`secure-input`(Mac이 잠겨 있거나 암호 입력란이 보안 입력을 쥐고 있어 프레임을 보낼 수 없음), `session-stopped`(시작하는 사이에 킬 스위치·회수·강등·페어링 키 재생성이 일어남). 본문이 틀리면 `400` `bad-request`.
  - **첫 offer는 이 응답 뒤에 온다.** Mac은 응답을 봉인한 다음에 `screen-offer`를 보내고, 그 전에 모은 ICE 후보는 offer 뒤에 순서대로 보낸다. 그래서 휴대폰은 `sessionId`를 모르는 offer·후보를 받지 않는다. 연결이 30초 안에 `connected`에 이르지 않으면 Mac이 세션을 끝낸다(`peer-left`).
- `POST /m1/screen-share/control-key` 본문 `{"publicKeyB64":"…"}`(ANSI X9.62 비압축 P-256 공개키 65바이트, base64) → `200 {"fingerprint":"XXXX-XXXX-XXXX-XXXX"}`
  - 허용 목록에 있고 `grant`가 `control`인 휴대폰만 등록할 수 있다(`device-not-allowed`·`insufficient-grant`).
  - **저장된 키가 없을 때만** 받는다. Mac은 받은 키의 지문을 화면에 띄우고 Mac 사용자가 휴대폰에 보이는 지문과 같다고 확인해야 저장한다. 거절하면 `403` `control-key-not-confirmed`, 확인 창이 이미 떠 있으면 `409` `control-key-pending`.
  - 같은 키를 다시 보내면 같은 지문으로 `200`(응답을 못 받은 재시도). **다른 키는 `409` `control-key-present`** — Mac은 키를 조용히 바꾸지 않는다. 휴대폰이 키를 잃었으면 Mac 사용자가 조작 권한을 거두었다가 다시 주어야 하고, 권한을 거두면 저장된 키도 지워진다.
  - 모양이 틀린 키는 `400` `bad-request`.
- 세션이 열리면 Mac은 알림을 한 번 띄우고 '원격 조작 중' 표시와 메뉴바 항목을 보여 준다. 세션마다 Mac에서 따로 승인을 묻지는 않는다.

### 새 암호화 메시지 타입

공통 봉투는 `{"type":"screen-…","sessionId":"<세션 id>", …}`다. 평문 JSON 하나는 64 KiB를 넘기지 않으며(릴레이 프레임 한도 1 MiB, 소켓 송신 버퍼 4 MiB에서 연결 종료), ICE 후보는 묶지 않고 생기는 대로 한 프레임에 하나씩 보낸다(트리클). 클립보드처럼 큰 데이터는 이 채널이 아니라 피어 연결의 데이터 채널로 간다.

| type | 방향 | 본문 | 설명 |
|---|---|---|---|
| `screen-offer` | 호스트 → 클라이언트 | `sdp`, `mode`, `displayId`, `codec`, `quality`, `iceRestart` | 영상을 보내는 쪽이 Mac이므로 offer도 Mac이 만든다. SDP에는 화면 글자 가독성을 위한 설정(contentHint `text`/`detail`, degradationPreference `maintain-resolution`)이 반영된다. 디스플레이 전환이나 TURN 자격증명 교체 때는 `iceRestart:true`로 다시 보낸다 |
| `screen-answer` | 클라이언트 → 호스트 | `sdp` | 휴대폰의 SDP answer |
| `screen-ice` | 양방향 | `candidate`, `sdpMid`, `sdpMLineIndex`, `usernameFragment` | ICE 후보 1개. 빈 `candidate`(`""`)는 후보 끝을 뜻한다 |
| `screen-session-end` | 양방향 | `reason` | 정상 종료. 휴대폰 쪽 이유: `user-stop`, `background`(앱이 백그라운드로 간 뒤 30초), `peer-failed`. 호스트 쪽 이유: `idle-timeout`(조작 10분·보기 30분), `background`(아래 `screen-background` 뒤 30초), `peer-left`, `display-gone` |
| `screen-background` | 클라이언트 → 호스트 | `background`(bool) | 휴대폰 앱이 백그라운드로 갔다(`true`)·돌아왔다(`false`). **30초 규칙은 Mac이 집행한다**: `true` 뒤 30초 안에 `false`가 오지 않으면 Mac이 세션을 끝내고 `screen-session-end`(`background`)를 보낸다. 아무 말 없이 조용해진 휴대폰은 유휴 타임아웃에 걸린다 |
| `screen-grant` | 호스트 → 클라이언트 | `allowed`, `grant`, `controlChallengeB64`?, `iceServers`?, `displays`? | 허용 목록과 보기/조작 권한이 Mac 설정에서 바뀌었음을 알린다(부여·승격·강등·회수). TURN 자격증명을 만료 전에 교체할 때도 이 메시지로 새 `iceServers`를 보내고 뒤이어 `iceRestart:true` offer를 보낸다. 세션 밖에서 보낼 때는 `sessionId`를 생략한다 |
| `screen-kill` | 호스트 → 클라이언트 | `reason`, `sessionId`? | 즉시 중단. `reason`: `revoked`, `grant-downgrade`, `rekey-pairing`, `kill-switch`, `lock-screen`, `secure-input`, `concurrency-limit`. `sessionId`가 없으면 그 휴대폰의 모든 세션이다 |

### 피어 연결: 영상 트랙 두 개와 데이터 채널

Mac이 만드는 offer에는 보내기 전용 영상 트랙 두 개와 데이터 채널 하나가 있다.

| 이름 | 무엇 | 비고 |
|---|---|---|
| 스트림 id `screen` (첫 번째 영상 트랙) | 사용자가 읽는 화면. 확대하지 않았으면 디스플레이 전체, 확대했으면 그 영역만 전체 화질로 | 네트워크별 상한(위 `quality`)과 TURN 경로의 2 Mbps 상한이 이 트랙에 걸린다 |
| 스트림 id `overview` (두 번째 영상 트랙) | 확대 중일 때 깔아 주는 디스플레이 전체의 저해상도 화면(가로 최대 640 px, 2 fps, 최대 150 kbps) | 확대하지 않았으면 프레임이 하나도 없다 |
| 데이터 채널 `screen-control` (ordered·reliable) | 아래 입력·클립보드 메시지 | Mac이 연다 |

두 트랙 모두 화면용 소스(libwebrtc의 screencast 소스 — 그 안에서 contentHint `text`/`detail`이 되는 설정)이고 degradationPreference는 `maintain-resolution`이다. 코덱은 하드웨어 H.264가 기본이며 HEVC는 offer에 넣지 않는다. 캡처는 Mac 하나에 하나이므로, 확대와 디스플레이 전환은 **조작 중인 휴대폰이거나 혼자 보고 있는 휴대폰**만 할 수 있다(보기 전용 휴대폰이 조작하는 사람의 화면을 바꾸지 못하게). 화면이 멈춰 있으면 프레임을 보내지 않고, 늦게 붙은 디코더를 위해 5초에 한 번만 마지막 화면을 다시 보낸다.

### 데이터 채널 메시지 형식 (`screen-control`, 릴레이를 지나지 않음)

**이 형식이 Mac과 휴대폰의 기준이다.** 메시지 하나는 UTF-8 JSON 텍스트 한 개이고 **64 KiB를 넘지 않는다**(넘으면 Mac이 버린다). 봉투는 `{"t":"…", …}`이며, Mac은 모르는 `t`·모양이 틀린 메시지를 내용을 기록하지 않고 조용히 버린다. 입력 이벤트는 압축하지 않는다.

좌표는 `displayId` + 정규화된 0–1 `x`/`y`(0이 왼쪽·위)이다. Mac이 `CGDisplayBounds(displayId)`에 맞춰 환산하고, 범위 밖 값은 0–1로 자르며, 그 디스플레이가 빠졌으면 주 디스플레이로 되돌린다. 확대 중에도 좌표는 **디스플레이 전체** 기준이다(휴대폰이 확대 영역 안의 터치를 디스플레이 좌표로 바꿔 보낸다).

| t | 방향 | 필드 | Mac이 하는 일 |
|---|---|---|---|
| `tap` | 휴대폰 → Mac | `displayId`, `x`, `y`, `button`: `"left"`\|`"right"`, `marker`? (`[A-Za-z0-9_-]` 1–32자) | 그 지점에서 클릭 한 번. 오른쪽 클릭(길게 누르기)은 `"right"`. `marker`가 있으면 클릭이 받아들여졌을 때 그 지점에 표식을 그리고 `marker`로 답한다. 모양이 틀린 `marker`는 탭째로 버린다 |
| `drag` | 휴대폰 → Mac | `displayId`, `x`, `y`, `phase`: `"begin"`\|`"move"`\|`"end"` | 왼쪽 버튼 누름·끌기·놓기 |
| `scroll` | 휴대폰 → Mac | `displayId`, `x`, `y`, `dx`, `dy` (화면 비율, 아래·오른쪽이 +) | 포인터를 그 지점에 두고 줄 단위로 스크롤. 1 % ≈ 1줄, 한 번에 ±120줄까지 |
| `text` | 휴대폰 → Mac | `text` (UTF-8 4096바이트 이하, 빈 문자열 불가) | **확정된 문자열**을 주입한다. 한글은 휴대폰 IME가 조합을 끝낸 글자만 온다. Mac은 `CGEventKeyboardSetUnicodeString`으로 UTF-16 20단위 이하씩 나눠 보내며, 한 글자(자소 결합·이모지 포함)를 두 조각으로 자르지 않는다 |
| `key` | 휴대폰 → Mac | `combo` | 키 조합 한 번(누름·뗌). 문법은 아래 |
| `zoom` | 휴대폰 → Mac | `displayId`, `region`: `{x, y, width, height}` (0–1) | 그 영역을 `screen` 트랙에 전체 화질로, 디스플레이 전체를 `overview` 트랙에 보낸다. 변이 2 % 미만이면 버리고, 디스플레이 밖으로 나간 부분은 잘라 낸다. `{0,0,1,1}`이면 확대 해제 |
| `display` | 휴대폰 → Mac | `displayId` | 그 디스플레이로 캡처를 바꾸고(없으면 주 디스플레이) 모든 휴대폰에 `iceRestart:true` offer를 다시 보낸다 |
| `clipboard` | 양방향 | 아래 | 클립보드 한 조각 |
| `clipboard-request` | 휴대폰 → Mac | (없음) | Mac 클립보드를 한 번 보낸다(`dir:"to-phone"` 조각들) |
| `marker` | Mac → 휴대폰 | `id`, `shown`(bool) | `marker`가 붙은 `tap`의 답. 표식이 화면에 올라간 뒤 보낸다. `shown:false`는 탭이 거절됐거나(보기 전용·잠금·보안 입력·사람 입력 뒤 2초) 표식을 못 그렸다는 뜻이다 |
| `scene` | Mac → 휴대폰 | `phase`: `"preroll"`\|`"motion"`\|`"still"`\|`"done"` | Mac에서 측정 장면의 단계가 바뀌었다. 연결된 모든 휴대폰(보기 전용 포함)에 보낸다 |

**`key`의 `combo` 문법.** 소문자, `+`로 잇는다: 수식키 0–4개(`ctrl`, `opt`, `shift`, `cmd`, 각 한 번씩) 뒤에 키 이름 정확히 하나. 키 이름은 닫힌 목록이다: `a`–`z`, `0`–`9`, `return`, `tab`, `space`, `backspace`, `delete`(앞으로 지우기), `escape`, `left`, `right`, `up`, `down`, `home`, `end`, `pageup`, `pagedown`. 예: `cmd+c`, `cmd+v`, `shift+cmd+z`, `opt+left`, `return`. 목록 밖의 이름·가상 키 코드·같은 수식키 두 번은 거절한다.

**`clipboard` 조각 (양방향·수동 버튼·조작 권한·최대 1 MB).** 모든 필드가 필수다.

```json
{"t":"clipboard","dir":"to-mac","id":"<전송 id, 1–64자>","seq":0,"total":3,
 "enc":"zstd","bytes":70000,"data":"<이 조각의 base64>"}
```

- `dir`: 휴대폰이 보내는 것은 언제나 `"to-mac"`, Mac이 보내는 것은 `"to-phone"`. Mac은 `"to-mac"`이 아닌 조각을 받지 않는다.
- 보내는 쪽은 클립보드 한 번 읽은 것을 **혼자** 압축(`enc:"zstd"`, 압축이 오히려 크면 `"raw"`)한 뒤 바이트열을 잘라 조각마다 base64로 담는다. 조각 하나의 원본은 32 KiB(Mac이 보내는 크기) — 어느 쪽이든 메시지 한 개가 64 KiB 안에 들어가면 된다. `total`은 1–64, `seq`는 0부터 `total-1`.
- `bytes`는 압축 전 전체 평문 바이트 수(1 이상 1 MiB 이하)이고 모든 조각에 같은 값이 들어간다. Mac은 **조각을 하나도 모으기 전에** 이 값을 확인하고, 모은 크기도 1 MiB를 넘으면 버린다. 압축을 푼 결과가 1 MiB를 넘거나(압축 폭탄) UTF-8이 아니면 버린다. 받는 쪽은 `enc` 태그를 읽고 추측하지 않는다.
- 한 세션에서 한 번에 한 전송만 모은다. 새 `id`가 오면 앞의 미완성 전송은 버리고, 30초 안에 끝나지 않은 전송도 버린다. `id`·`enc`·`bytes`·`total`이 앞 조각과 다르면 그 전송을 버린다.
- Mac은 조각을 다 모은 뒤 **조작 권한이 그때도 살아 있는지 다시 확인한 뒤** Mac 클립보드에 쓴다. ⌘V를 대신 누르지는 않는다 — 붙여넣기는 휴대폰이 `key` `cmd+v`로 따로 보낸다.
- Mac 쪽 클립보드가 비밀로 표시된 항목(`org.nspasteboard.ConcealedType` 등, 암호 관리자)이면 읽지 않고 `{"t":"clipboard","dir":"to-phone","id":"…","seq":0,"total":1,"enc":"raw","bytes":0,"data":"","concealed":true}` 하나만 보낸다.

**판단은 전부 Mac이 한다.** 보기 전용 세션의 `tap`·`drag`·`scroll`·`text`·`key`·`clipboard`·`clipboard-request`는 전부 거절된다. 조작 세션이어도 잠금 화면·보안 입력 중이거나, Mac에서 사람이 키보드·마우스를 쓴 뒤 2초 동안은 거절된다. 주입은 안전 정책(`ScreenShareService.deliver`)을 지난 것만 `CGEventPost`에 닿고, Mac이 주입한 이벤트에는 표식(`eventSourceUserData`)이 붙어 "사람이 Mac을 쓴 것"으로 오인되지 않는다. 입력한 글자·키는 어디에도 기록하지 않는다.

### 탭 표식과 측정 장면

탭→화면 지연은 휴대폰 시계로 잰다. Mac은 화면이 멈춰 있으면 프레임을 보내지 않으므로, 탭이 화면을 바꾸지 않으면 잴 프레임이 없다. 그래서 측정 중인 휴대폰은 `tap`에 `marker` id를 붙이고(상태의 `tapMarker:true`일 때만), Mac은:

1. 클릭을 평소처럼 안전 정책(`ScreenShareService.deliver`)에 넘긴다. 표식은 조작 권한을 넓히지 않는다 — 받아들여진 탭에만 그린다.
2. 받아들여졌으면 Mac이 환산한 그 지점에 지름 48pt의 검정·흰색·자홍 고리를 0.5초 동안 그린다. 테두리 없는 창이고 클릭을 통과시키며(포커스를 빼앗지 않는다) 모든 창 위에 뜬다. ScreenCaptureKit은 이 앱의 창도 캡처하므로 표식은 탭의 효과와 같은 스트림으로 간다.
3. 표식이 화면에 올라간 뒤 `{"t":"marker","id":"…","shown":true}`를 데이터 채널로 보낸다. 거절된 탭은 그리지 않고 `shown:false`로 답한다.

휴대폰은 탭을 보낸 시각을 기록하고, `shown:true` 답을 받는 즉시 `getStats`를 읽어 디코드된 프레임 수를 기준으로 삼은 뒤, 그 수를 넘는 첫 읽기까지를 탭→화면 시간으로 센다. 영상은 인코드·지터 버퍼·디코드를 거치므로 표식 프레임은 거의 언제나 답보다 늦게 도착한다. 답이 오기까지의 시간(입력 경로)도 따로 기록한다. `shown:false`면 그 표본을 버리고 거절로 센다. 2초 안에 끝나지 않은 표본은 시간 초과다. `tapMarker`가 없는 Mac에는 `marker`를 보내지 않고, 탭 직후 기준을 잡아 첫 새 프레임까지 재는 예전 방식을 쓴다(멈춘 화면에서만 의미가 있다).

**측정 장면.** Mac 설정의 화면 보기·조작 항목에 있는 **측정용 장면 재생**은 주 디스플레이에 전용 창을 띄워 항상 같은 장면을 재생한다. 사용자의 다른 앱이나 문서는 건드리지 않는다.

| 단계 | 시작 | 길이 | 화면 |
|---|---|---|---|
| `preroll` | 0초 | 3초 | 창만 뜨고 움직이지 않는다(카운트다운) |
| `motion` | 3초 | 60초 | 문서가 초당 90pt로 올라가고, 옆의 터미널에 초당 18자가 입력된다. 남은 초가 보인다 |
| `still` | 63초 | 30초 | 아무 픽셀도 바뀌지 않는다. 카운트다운 대신 끝나는 시각을 한 번만 보여 준다 |
| `done` | 93초 | — | 끝. 도중에 창을 닫아도 `done`을 보낸다 |

단계가 바뀔 때마다 Mac은 연결된 모든 휴대폰에 `scene`을 보내고, 휴대폰은 측정 내보내기에 단계와 시각을 남긴다. 그래서 움직일 때·멈춰 있을 때의 비트레이트와 지연을 장면 단계로 나눠 볼 수 있다. 장면을 재생하는 동안 휴대폰이 연결되어 있지 않았다면 위 표의 시각으로 나눈다.

### 중단은 Mac이 한다

`screen-kill`은 **알려 주는 메시지일 뿐 방어선이 아니다.** 기기 해제, 권한 강등·회수, 페어링 키 다시 만들기, 메뉴바 킬 스위치와 단축키(⌃⌥⌘K)는 Mac에서 SCStream 캡처를 멈추고 입력 주입을 막고 PeerConnection을 닫는 것으로 끝나며, 릴레이가 죽어 있어도 1초 안에 끝난다. Mac은 방아쇠 시점 t0과 전부 멈춘 시점 t1을 자기 시계로 기록한다. 메시지는 보낼 수 있으면 보내는 것이고, 휴대폰은 메시지를 못 받아도 영상이 끊기는 것으로 알게 된다.

- 잠금 화면이나 보안 입력(암호 입력란)에서는 프레임이 멈추고 주입이 거절되며, 휴대폰에 그 상태가 표시된다.
- Mac에서 사람이 직접 키보드·마우스를 쓰면 원격 입력은 2초 쉬어 간다.
- 좌표 규약은 `displayId` + 정규화된 0–1 좌표이고, Mac이 `CGDisplayBounds`에 맞춰 환산한다. 보던 디스플레이가 빠지면 주 디스플레이로 되돌린다.
- 세션 기록은 기기·시작/종료 시각·모드만 남긴다. 입력한 키는 어디에도 남기지 않는다.

## 릴레이 실행

```
cd relay && npm install && npm start            # ws://0.0.0.0:8787
docker build -t mightyclaude-relay relay && docker run -p 8787:8787 mightyclaude-relay
```

공개 인터넷에서는 TLS가 있는 리버스 프록시(Caddy, Cloudflare 등) 뒤에 두고 `wss://`로 쓴다. 릴레이 자체는 어떤 비밀도 알지 못한다.
