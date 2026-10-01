// 용법: node relay/deploy/oracle/turn-mint-check.mjs [도메인]
//
// 살아 있는 릴레이의 TURN 자격증명 발급을 확인한다. 호스트 제어 소켓을 하나
// 열고(serverId = SHA-256(hostToken), docs/relay.md) 평문 host→relay 메시지
// `turn-credentials-request`를 보낸 뒤 `turn-credentials` 응답의 모양을 검사한다.
// 비밀번호는 길이만 찍고 값은 찍지 않는다. coturn use-auth-secret은 릴레이
// 안에만 있으므로 이 검사로도 밖으로 새지 않는다.
import { createHash, randomBytes } from 'node:crypto';

const args = process.argv.slice(2);
// --emit-credential: 사람이 읽는 줄은 stderr로, '<username>\t<password>' 한 줄만
// stdout으로 보낸다. 다른 검사(turn-allocate-check.py)에 파이프로 넘기기 위한 것이고,
// 로그로 남기면 안 된다.
const emit = args.includes('--emit-credential');
const domain = args.find((a) => !a.startsWith('--')) ?? 'mightyclaude.duckdns.org';
const hostToken = randomBytes(32).toString('hex');
const serverId = createHash('sha256').update(hostToken, 'utf8').digest('hex');
const url = `wss://${domain}/ws?v=1&role=server&serverId=${serverId}&hostToken=${hostToken}`;

function done(ok, line, credential = null) {
  const text = `${ok ? '  ok  ' : '  FAIL'} ${line}`;
  if (emit) process.stderr.write(`${text}\n`);
  else console.log(text);
  if (ok && emit && credential !== null) process.stdout.write(credential);
  process.exit(ok ? 0 : 1);
}

const ws = new WebSocket(url);
const timer = setTimeout(() => done(false, '15초 안에 turn-credentials 응답이 없습니다'), 15_000);

ws.onerror = () => done(false, `제어 소켓을 열지 못했습니다: wss://${domain}/ws`);
ws.onopen = () => ws.send(JSON.stringify({ type: 'turn-credentials-request' }));
ws.onmessage = (event) => {
  clearTimeout(timer);
  let msg;
  try {
    const text = typeof event.data === 'string' ? event.data : Buffer.from(event.data).toString('utf8');
    msg = JSON.parse(text);
  } catch {
    done(false, '응답이 JSON이 아닙니다');
  }
  try { ws.close(); } catch { /* 이미 닫힘 */ }

  if (msg.type !== 'turn-credentials') {
    done(false, `turn-credentials가 아니라 ${msg.type}를 받았습니다`);
  }
  const problems = [];
  if (!/^\d{10,}:[0-9a-f]{16}$/.test(msg.username ?? '')) problems.push(`username 형식: ${msg.username}`);
  if (typeof msg.password !== 'string' || msg.password.length < 20) problems.push('password 길이');
  if (msg.ttl !== 3600) problems.push(`ttl=${msg.ttl} (3600 기대)`);
  if (!Array.isArray(msg.uris) || !msg.uris.some((u) => u.startsWith(`turn:${domain}:`))) {
    problems.push(`uris=${JSON.stringify(msg.uris)}`);
  }
  const expiry = Number((msg.username ?? '0:').split(':')[0]);
  const left = expiry - Math.floor(Date.now() / 1000);
  if (left < 3000 || left > 3700) problems.push(`만료까지 ${left}초`);

  if (problems.length > 0) done(false, `turn-credentials 내용 이상 — ${problems.join(', ')}`);
  done(
    true,
    `turn-credentials 발급: uris=${msg.uris.join(',')} ttl=${msg.ttl}s 만료까지=${left}s password=<${msg.password.length}자 가림>`,
    `${msg.username}\t${msg.password}`,
  );
};
