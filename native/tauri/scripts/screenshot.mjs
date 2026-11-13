// Takes a picture of the Tauri app's page through WebView2's DevTools protocol, once the sidebar shows its panes.
//
//   MightyCode.exe --profile <folder> --remote-debugging-port 9222
//   node native/tauri/scripts/screenshot.mjs 9222 <out.png>
//
// Node 22's built-in WebSocket and fetch; no packages.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';

const [port = '9222', out = 'skeleton.png'] = process.argv.slice(2);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function target() {
  for (let attempt = 0; attempt < 120; attempt++) {
    try {
      const pages = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = pages.find((p) => p.type === 'page' && /tauri\.localhost|localhost:5173/.test(p.url));
      if (page) return page;
    } catch {
      // Not listening yet.
    }
    await sleep(250);
  }
  throw new Error(`no page on port ${port}`);
}

const page = await target();
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {
  socket.onopen = resolve;
  socket.onerror = reject;
});
let next = 0;
const waiting = new Map();
socket.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && waiting.has(message.id)) {
    waiting.get(message.id)(message);
    waiting.delete(message.id);
  }
};
const send = (method, params = {}) =>
  new Promise((resolve) => {
    const id = ++next;
    waiting.set(id, resolve);
    socket.send(JSON.stringify({ id, method, params }));
  });
const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true })).result?.result?.value;

for (let attempt = 0; attempt < 100 && !(await evaluate("document.querySelectorAll('.sidebar-pane-title').length > 0")); attempt++) await sleep(100);
await sleep(800);
const shot = await send('Page.captureScreenshot', { format: 'png' });
if (!shot.result?.data) throw new Error('no picture: ' + JSON.stringify(shot.error));
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, Buffer.from(shot.result.data, 'base64'));
console.log(`${out}: ${await evaluate('innerWidth + "x" + innerHeight')} css px, ${await evaluate("document.querySelectorAll('.sidebar-pane-title').length")} pane titles`);
socket.close();
