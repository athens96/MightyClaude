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
- 인증(암호화된 첫 메시지): 클라이언트 → `{"type":"auth","pairingKey":"…","clientName":"…"}`, 호스트 → `{"type":"auth_ok","hostName":"…","hostId":"…","appVersion":"…"}` 또는 `{"type":"auth_error","reason":"pairing-key"}`를 보낸 뒤 소켓을 닫음(클라이언트는 이를 재페어링 필요로 표시). 호스트는 `auth_ok` 전에는 다른 메시지를 처리하지 않는다.

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

무엇이 어디로 흐르는지:

| 내용 | 경로 |
|---|---|
| offer·answer·ICE 후보, 세션 종료, 권한, 킬 | 릴레이의 E2EE 채널 (이 절의 메시지 타입) |
| 영상 프레임 | WebRTC 피어 연결 (host → srflx/prflx → 최후에 TURN relay) |
| 입력 이벤트(포인터·스크롤·키·확정된 문자열), 클립보드(양방향·수동 버튼·최대 1 MB·zstd) | 피어 연결의 데이터 채널. 릴레이를 지나지 않으므로 릴레이 프레임 한도와 무관하다 |
| getStats 지표(RTT·지터·디코드 시간·프리즈·비트레이트, 선택된 후보 종류 `host`/`srflx`/`prflx`/`relay`) | 휴대폰 안에서만 집계·표시. 릴레이로 보내지 않는다 |

### 호스트 기능 광고 (`screenShare`)

기능 광고는 기존 방식을 그대로 쓴다. 호스트는 `GET /m1/info`의 `capabilities` 배열(`docs/mobile-remote.md`의 `MobileInfo.capabilities`)에 이름 하나를 더한다: **`"screenShare"`**.

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

- `GET /m1/screen-share/state` → `{"screenShare":{"allowed":bool,"grant":"none"|"view"|"control","isBeta":true,"displays":[{"displayId":N,"width":N,"height":N,"main":bool}],"controlChallengeB64":"…","iceServers":[…],"idleTimeoutSeconds":600}}`
  - `controlChallengeB64`는 이 세션 한 번만 쓰는 바이트열(`screen-control-challenge:<sessionId>:<unix초>`)이며 `grant`가 `control`일 때만 들어 있다.
  - `iceServers`는 릴레이가 발급한 짧은 수명(기본 1시간)의 TURN 자격증명이다(위 [TURN 자격증명 발급](#turn-자격증명-발급-릴레이-평문-e2ee-아님)). coturn 비밀값은 릴레이에만 있고 Mac·휴대폰에는 절대 오지 않는다.
  - `idleTimeoutSeconds`는 조작 600초, 보기 전용 1800초다.
- `POST /m1/screen-share/sessions` 본문 `{"mode":"view"|"control","displayId":N,"controlSignatureB64":"…","network":"wifi"|"cellular","decodes":["H264","VP9","AV1"]}` → `200 {"sessionId":"…","mode":"…","displayId":N,"codec":"H264"|"VP9"|"AV1","quality":{"width":N,"height":N,"fps":N,"maxBitrateKbps":N}}`
  - `mode:"control"`은 매 세션 `controlSignatureB64`가 필요하다. 생체·PIN이 걸린 Android Keystore 키(P-256)로 `controlChallengeB64`에 서명한 ECDSA DER 서명이고, 권한을 줄 때 등록해 둔 공개키(`controlKeyPublic`)로 **Mac이** 검증한다. 휴대폰 쪽 확인만으로는 조작 세션이 열리지 않는다.
  - `decodes`는 휴대폰이 `getCapabilities`로 확인한 디코딩 가능 코덱이다. 기본값은 하드웨어 H.264이고, 모바일 데이터에서 Mac CPU에 여유가 있고 휴대폰이 디코딩할 수 있을 때만 VP9/AV1을 고른다. CPU·발열 압박이 생기면 H.264로 되돌린다. HEVC는 쓰지 않는다.
  - `quality`는 네트워크별 상한이다: 와이파이 최대 1080p30(약 6 Mbps), 모바일 데이터 최대 720p15(약 1 Mbps, 5–15 fps). TURN relay 경로에서는 세션 대역 할당량(기본 2 Mbps)까지로 더 낮춘다. 화면이 멈춰 있으면 프레임을 보내지 않아 유휴 트래픽은 0에 가깝다.
  - 거절은 `403 {"error":{"reason":"…"}}`: `legacy-client`, `device-not-allowed`(허용 목록 밖 — 새로 페어링한 휴대폰의 기본값), `insufficient-grant`, `control-signature`, `concurrency-limit`(조작 1대 + 보기 전용 2대까지), `screen-permission`(Mac의 화면 기록 권한이 없거나 만료됨 — 휴대폰은 "Mac에서 승인 필요"를 보여 준다).
- 세션이 열리면 Mac은 알림을 한 번 띄우고 '원격 조작 중' 표시와 메뉴바 항목을 보여 준다. 세션마다 Mac에서 따로 승인을 묻지는 않는다.

### 새 암호화 메시지 타입

공통 봉투는 `{"type":"screen-…","sessionId":"<세션 id>", …}`다. 평문 JSON 하나는 64 KiB를 넘기지 않으며(릴레이 프레임 한도 1 MiB, 소켓 송신 버퍼 4 MiB에서 연결 종료), ICE 후보는 묶지 않고 생기는 대로 한 프레임에 하나씩 보낸다(트리클). 클립보드처럼 큰 데이터는 이 채널이 아니라 피어 연결의 데이터 채널로 간다.

| type | 방향 | 본문 | 설명 |
|---|---|---|---|
| `screen-offer` | 호스트 → 클라이언트 | `sdp`, `mode`, `displayId`, `codec`, `quality`, `iceRestart` | 영상을 보내는 쪽이 Mac이므로 offer도 Mac이 만든다. SDP에는 화면 글자 가독성을 위한 설정(contentHint `text`/`detail`, degradationPreference `maintain-resolution`)이 반영된다. 디스플레이 전환이나 TURN 자격증명 교체 때는 `iceRestart:true`로 다시 보낸다 |
| `screen-answer` | 클라이언트 → 호스트 | `sdp` | 휴대폰의 SDP answer |
| `screen-ice` | 양방향 | `candidate`, `sdpMid`, `sdpMLineIndex`, `usernameFragment` | ICE 후보 1개. 빈 `candidate`(`""`)는 후보 끝을 뜻한다 |
| `screen-session-end` | 양방향 | `reason` | 정상 종료. 휴대폰 쪽 이유: `user-stop`, `background`(앱이 백그라운드로 간 뒤 30초), `peer-failed`. 호스트 쪽 이유: `idle-timeout`(조작 10분·보기 30분), `peer-left`, `display-gone` |
| `screen-grant` | 호스트 → 클라이언트 | `allowed`, `grant`, `controlChallengeB64`?, `iceServers`?, `displays`? | 허용 목록과 보기/조작 권한이 Mac 설정에서 바뀌었음을 알린다(부여·승격·강등·회수). TURN 자격증명을 만료 전에 교체할 때도 이 메시지로 새 `iceServers`를 보내고 뒤이어 `iceRestart:true` offer를 보낸다. 세션 밖에서 보낼 때는 `sessionId`를 생략한다 |
| `screen-kill` | 호스트 → 클라이언트 | `reason`, `sessionId`? | 즉시 중단. `reason`: `revoked`, `grant-downgrade`, `rekey-pairing`, `kill-switch`, `lock-screen`, `secure-input`, `concurrency-limit`. `sessionId`가 없으면 그 휴대폰의 모든 세션이다 |

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
