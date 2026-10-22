// 용법: node relay/deploy/oracle/push-check.mjs [도메인]
//
// 살아 있는 릴레이의 푸시 경로를 확인한다(docs/relay.md "푸시 알림"). 임시 호스트
// 신원과 가짜 FCM 토큰으로 핸들을 하나 만들고 그 핸들로 보내 본다. 가짜 토큰이므로
// FCM은 거절해야 하고, 릴레이는 410(token-gone)으로 답하고 핸들을 지워야 한다.
// 503이면 서비스 계정 키가 없거나 읽히지 않은(깨진 JSON·개인 키 포함) 것이다.
// 실제 기기에는 아무것도 가지 않는다.
//
// 이 검사로는 서비스 계정 키가 앱의 google-services.json과 같은 Firebase 프로젝트인지
// 알 수 없다(가짜 토큰은 어느 프로젝트에서나 무효라서). 그것은 재배포 뒤 실제 기기로
// 알림 하나를 받아 보는 것으로만 확인된다. FCM이 다른 이유로 거절하면 그 FCM 오류
// 코드(예: SENDER_ID_MISMATCH)를 보여 준다.
import { createHash, randomBytes } from 'node:crypto';

const domain = process.argv[2] ?? 'mightyclaude.duckdns.org';
const base = `https://${domain}`;
const hostToken = randomBytes(32).toString('hex');
const serverId = createHash('sha256').update(hostToken, 'utf8').digest('hex');

async function post(path, body, bearer) {
  const headers = { 'content-type': 'application/json' };
  if (bearer) headers.authorization = `Bearer ${bearer}`;
  const res = await fetch(base + path, { method: 'POST', headers, body: JSON.stringify(body) });
  return { status: res.status, body: await res.text() };
}

const registered = await post('/push/v1/handles', { fcmToken: `push-check-${randomBytes(16).toString('hex')}` });
if (registered.status !== 201) {
  console.error(`✘ 핸들 등록이 ${registered.status}로 답했습니다: ${registered.body}`);
  process.exit(1);
}
const { handle } = JSON.parse(registered.body);
const forged = await post('/push/v1/send', { serverId, handle, payload: 'A'.repeat(64) }, randomBytes(32).toString('hex'));
const sent = await post('/push/v1/send', { serverId, handle, payload: 'A'.repeat(64) }, hostToken);

const problems = [];
if (forged.status !== 401) problems.push(`틀린 호스트 토큰이 401이 아니라 ${forged.status}`);
if (sent.status === 503) problems.push('FCM이 꺼져 있습니다(서비스 계정 키 없음 또는 읽기 실패)');
else if (sent.status !== 410) {
  let fcmError = null;
  try {
    fcmError = JSON.parse(sent.body).fcmError ?? null;
  } catch {}
  problems.push(
    fcmError === null
      ? `가짜 토큰 전송이 410이 아니라 ${sent.status}: ${sent.body}`
      : `가짜 토큰 전송이 410이 아니라 ${sent.status}, FCM 오류 코드 ${fcmError}`,
  );
}
if (problems.length > 0) {
  for (const line of problems) console.error(`✘ ${line}`);
  process.exit(1);
}
console.log('✔ 푸시 경로: 호스트 인증 401, FCM까지 가서 가짜 토큰을 410으로 지움');
console.log('  키의 Firebase 프로젝트가 앱과 같은지는 이 검사로 알 수 없습니다. 실제 기기로 알림 하나를 받아 보세요.');
