// 용법: node relay/deploy/oracle/host-auth-check.mjs [도메인]
//
// 보안 라운드(F-01)에서 올린 호스트 인증이 살아 있는 릴레이에 실제로 반영됐는지
// 확인한다. serverId가 SHA-256(hostToken)이 아닌 호스트 소켓은 4401로 끊겨야
// 하고, 맞는 소켓은 열려야 한다(docs/relay.md "릴레이 와이어").
import { createHash, randomBytes } from 'node:crypto';

const domain = process.argv[2] ?? 'mightyclaude.duckdns.org';

/**
 * 소켓 하나를 연다. 릴레이가 끊으면 {closed: code}, 1.5초를 버티면 {open: true}.
 * 거절도 WebSocket 핸드셰이크(101) 뒤 닫힘 코드로 오기 때문에, onopen이
 * 불렸는지로 판단하면 안 된다.
 */
function probe(serverId, hostToken) {
  return new Promise((resolve) => {
    const ws = new WebSocket(`wss://${domain}/ws?v=1&role=server&serverId=${serverId}&hostToken=${hostToken}`);
    const stayed = setTimeout(() => {
      try { ws.close(); } catch { /* 이미 닫힘 */ }
      resolve({ open: true });
    }, 1500);
    const give_up = setTimeout(() => resolve({ timeout: true }), 15_000);
    ws.onclose = (event) => {
      clearTimeout(stayed);
      clearTimeout(give_up);
      resolve({ closed: event.code });
    };
    ws.onerror = () => { /* onclose가 결과를 준다 */ };
  });
}

const goodToken = randomBytes(32).toString('hex');
const goodId = createHash('sha256').update(goodToken, 'utf8').digest('hex');
const wrongId = createHash('sha256').update(randomBytes(32).toString('hex'), 'utf8').digest('hex');

const forged = await probe(wrongId, goodToken);
const honest = await probe(goodId, goodToken);

const problems = [];
if (forged.closed !== 4401) {
  problems.push(`위조 serverId가 4401로 끊기지 않았습니다 (${JSON.stringify(forged)})`);
}
if (honest.open !== true) {
  problems.push(`정상 호스트 소켓이 유지되지 않았습니다 (${JSON.stringify(honest)})`);
}

if (problems.length > 0) {
  console.log(`  FAIL ${problems.join(', ')}`);
  process.exit(1);
}
console.log('  ok   호스트 인증 살아 있음: 위조 serverId는 4401, 정상 소켓은 열림');
