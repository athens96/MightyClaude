// Changes the theme in the running Tauri app through the page's own channel (host_post, as the theme button does) over
// WebView2's DevTools protocol, and waits until the page shows it.
//
//   MightyCode.exe --profile <folder> --allow-state-write --remote-debugging-port 9333
//   node native/tauri/tools/parity/theme.mjs 9333 light
//
// Node 22's built-in WebSocket and fetch; no packages.
const [port = '9333', theme = 'light'] = process.argv.slice(2);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function target() {
  for (let attempt = 0; attempt < 160; attempt++) {
    try {
      const pages = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
      const page = pages.find((p) => p.type === 'page' && /tauri\.localhost/.test(p.url));
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
const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })).result?.result?.value;

const until = async (expression, ms) => {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (await evaluate(expression)) return true;
    await sleep(100);
  }
  return false;
};

if (!(await until("document.querySelectorAll('.sidebar-pane-title').length > 0 || !!document.querySelector('[data-testid=status-bar]')", 30000))) {
  throw new Error('the page did not come up');
}
await evaluate(`window.__TAURI_INTERNALS__.invoke('host_post', { message: { kind: 'call', id: 990777, method: 'setTheme', args: { theme: '${theme}' } } }).then(() => true)`);
const shown = await until(`document.documentElement.dataset.theme === '${theme}'`, 10000);
console.log(JSON.stringify({ theme, shown }));
socket.close();
process.exit(shown ? 0 : 1);
