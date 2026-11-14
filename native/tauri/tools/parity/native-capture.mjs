// Captures the `native` and `text` pushes C#'s web shell sends its page (plan W6 §1.8), over WebView2's DevTools protocol, into a
// JSONL file the Rust side's `native-diff` example reads: one `{"seq":n,"message":{…}}` per push, the message as the page got it.
//
//   set WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9334
//   MightyCode.exe --smoke-test --smoke-exit --profile <empty temp folder>        (with MIGHTY_SMOKE_WEB=1)
//   node native/tauri/tools/parity/native-capture.mjs 9334 cs.jsonl [seconds]
//
// It waits for the page at https://app.mighty.invalid/, taps chrome.webview's messages in every document the page loads, drains the
// tap every 100 ms and ends when the app goes away (or after `seconds`, 600 by default). Node 22's built-in WebSocket and fetch; no
// packages.
import { appendFileSync, writeFileSync } from 'node:fs';

const [port = '9334', out = 'cs.jsonl', seconds = '600'] = process.argv.slice(2);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const deadline = Date.now() + Number(seconds) * 1000;

async function target() {
  for (let attempt = 0; attempt < 600; attempt++) {
    try {
      const pages = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = pages.find((p) => p.type === 'page' && /app\.mighty\.invalid/.test(p.url));
      if (page) return page;
    } catch {
      // Not listening yet.
    }
    await sleep(250);
  }
  throw new Error(`no page on port ${port}`);
}

// The tap: the messages the page gets whose kind is native or text, as JSON text in the order they came.
const TAP = `(() => {
  if (window.__nativeTap) return true;
  if (!window.chrome || !window.chrome.webview) return false;
  window.__nativeTap = [];
  window.chrome.webview.addEventListener('message', (e) => {
    const d = e.data;
    if (d && (d.kind === 'native' || d.kind === 'text')) window.__nativeTap.push(JSON.stringify(d));
  });
  return true;
})()`;

const page = await target();
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = reject;
});
let next = 0;
let closed = false;
const waiting = new Map();
socket.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && waiting.has(message.id)) {
    waiting.get(message.id)(message);
    waiting.delete(message.id);
  }
};
socket.onclose = () => {
  closed = true;
  for (const resolve of waiting.values()) resolve({});
  waiting.clear();
};
const send = (method, params = {}) =>
  new Promise((resolve) => {
    if (closed) return resolve({});
    const id = ++next;
    waiting.set(id, resolve);
    socket.send(JSON.stringify({ id, method, params }));
  });
const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true })).result?.result?.value;

writeFileSync(out, '');
await send('Page.addScriptToEvaluateOnNewDocument', { source: TAP });
let seq = 0;
while (!closed && Date.now() < deadline) {
  await evaluate(TAP);
  const drained = await evaluate('JSON.stringify(window.__nativeTap ? window.__nativeTap.splice(0) : [])');
  if (typeof drained === 'string') {
    const lines = JSON.parse(drained).map((raw) => `{"seq":${++seq},"message":${raw}}\n`);
    if (lines.length) appendFileSync(out, lines.join(''));
  }
  await sleep(100);
}
console.log(JSON.stringify({ out, pushes: seq, ended: closed ? 'app closed' : 'time up' }));
if (!closed) socket.close();
process.exit(0);
