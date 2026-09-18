# MightyClaude 릴레이

휴대폰(Expo 앱)과 Mac 호스트가 **각자 바깥으로 접속**해 만나는 WebSocket 파이프다.
포트 개방·VPN이 필요 없고, 릴레이는 `serverId`/`connectionId`로 소켓을 짝지어 프레임을 그대로 전달할 뿐이다.
명세는 [`../docs/relay.md`](../docs/relay.md)의 "릴레이 와이어" 절이다.

## 릴레이는 암호문만 본다

데이터 소켓의 페이로드는 호스트와 클라이언트가 X25519 + ChaCha20-Poly1305로 종단 간 암호화한다.
릴레이는 키를 만들지도, 받지도, 저장하지도 않으며 프레임 내용을 해석할 수 없다.
평문으로 보는 것은 쿼리(`serverId`, `role`, `connectionId`, `v`)와 제어 소켓 알림(`connected`/`disconnected`)뿐이다.
로그에도 프레임 내용은 절대 남기지 않고 `serverId` 앞 8자만 찍는다.

## 로컬 실행

```bash
cd relay
npm install
npm run build && npm start     # ws://0.0.0.0:8787/ws , http://localhost:8787/healthz
npm run dev                    # 코드 변경 시 자동 재시작 (node --watch, 타입 스트리핑)
npm test                       # vitest
```

### 환경 변수

| 변수 | 기본값 | 설명 |
|---|---|---|
| `HOST` | `0.0.0.0` | 바인드 주소 |
| `PORT` | `8787` | 포트 |
| `RELAY_ATTACH_TIMEOUT_MS` | `10000` | 클라이언트가 호스트 데이터 소켓을 기다리는 시간 |
| `RELAY_PING_INTERVAL_MS` | `30000` | WebSocket ping 주기(응답 없으면 다음 주기에 종료) |
| `RELAY_MAX_CONNECTIONS` | `32` | `serverId`당 동시 연결 수 |
| `RELAY_MAX_BUFFERED_FRAMES` | `64` | 호스트 부착 전 버퍼링할 클라이언트 프레임 수 |
| `RELAY_MAX_PAYLOAD` | `1048576` | 프레임 최대 크기(바이트) |

## Docker

```bash
docker build -t mightyclaude-relay relay
docker run --rm -p 8787:8787 mightyclaude-relay
curl http://localhost:8787/healthz   # -> ok
```

이미지는 `node:22-alpine` 기반이고 비루트 사용자(`node`)로 실행된다. 상태를 디스크에 쓰지 않으므로 볼륨이 필요 없고, 여러 인스턴스를 띄울 경우 같은 `serverId`의 소켓들이 **같은 인스턴스**로 가도록 스티키 라우팅이 필요하다.

## 공개 인터넷: TLS 리버스 프록시 뒤에 두기

릴레이 자체는 TLS를 종단하지 않는다. 항상 프록시 뒤에 두고 클라이언트는 `wss://`로 접속한다.

### Caddy

```caddyfile
relay.example.com {
    reverse_proxy 127.0.0.1:8787
}
```

Caddy는 인증서를 자동 발급하고 WebSocket 업그레이드를 그대로 통과시킨다.
페어링 URL의 `relay=` 값은 `wss://relay.example.com`이 된다.

### Cloudflare

1. `relay.example.com` A/CNAME 레코드를 오리진으로 두고 프록시(주황 구름)를 켠다.
2. SSL/TLS 모드는 **Full (strict)** 를 쓰고, 오리진은 Caddy/Cloudflare Tunnel로 TLS를 종단한다.
3. WebSocket은 기본으로 켜져 있다(Network → WebSockets). 끄면 업그레이드가 실패한다.
4. Cloudflare의 유휴 연결 정리 때문에 100초 무통신 연결이 끊길 수 있는데, 릴레이의 30초 ping과 암호 채널의 20초 ping이 이를 막는다.
5. Cloudflare Tunnel(`cloudflared`)을 쓰면 오리진 포트를 열지 않아도 된다: `cloudflared tunnel --url http://127.0.0.1:8787`.

## 프로토콜 요약

| 소켓 | 쿼리 | 동작 |
|---|---|---|
| 호스트 제어 | `serverId&role=server&v=1` | `serverId`당 1개. 새 소켓이 오면 이전 소켓은 4409로 닫히고 대기 상태는 새 소켓이 이어받는다. `{"type":"connected"\|"disconnected","connectionId"}` 알림 수신 |
| 클라이언트 데이터 | `serverId&role=client&connectionId&v=1` | 제어 소켓 없으면 4404. 있으면 호스트 데이터 소켓을 기다리며 최대 64프레임 버퍼(초과 4413), 시간 초과 4504 |
| 호스트 데이터 | `serverId&role=server&connectionId&v=1` | 대기 중인 클라이언트가 없으면 4404. 있으면 양방향 파이프(버퍼는 순서대로 먼저 전달) |

종료 코드: `4400` 잘못된 쿼리, `4404` 대상 없음, `4409` 제어 소켓 교체, `4410` 호스트 오프라인, `4413` 버퍼 초과, `4429` 연결 수 초과, `4504` 호스트 부착 시간 초과.
한쪽 데이터 소켓이 닫히면 반대쪽도 **같은 코드·사유**로 닫히고 제어 소켓에 `disconnected`가 간다.

## 라이선스

이 릴레이는 MightyClaude의 일부로 MIT 라이선스입니다. 릴레이 구조는 Apache License 2.0으로 배포되는 Paseo를 참고해 설계했으며, 고지는 저장소 루트의 `NOTICE.md`를 참고하세요.
