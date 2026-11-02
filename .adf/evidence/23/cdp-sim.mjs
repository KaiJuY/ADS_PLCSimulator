// Opens the running Host's /simulation/ page in headless Edge and records it while scenarios run.
// Usage: node cdp-sim.mjs <outdir> <seconds> [intervalMs] [reloadAtSeconds]
// Writes frame-<ms>.png, frames.jsonl (time, status line, visible labels, error rows) and errors.json (page exceptions).
import { spawn } from 'node:child_process';
import { appendFileSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [outDir, seconds, intervalArg = '500', reloadAt] = process.argv.slice(2);
const interval = Number(intervalArg);
mkdirSync(outDir, { recursive: true });
const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const profile = mkdtempSync(join(tmpdir(), 'edge23i-'));
const proc = spawn(edge, ['--headless=new', '--disable-gpu', '--remote-debugging-port=9334', `--user-data-dir=${profile}`, '--window-size=1000,760', 'about:blank'], { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let targets;
for (let i = 0; i < 50 && !targets; i++) { try { targets = await (await fetch('http://127.0.0.1:9334/json/list')).json(); } catch { await sleep(200); } }
const page = targets.find((t) => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r));
let id = 0;
const pending = new Map();
const errors = [];
ws.addEventListener('message', (e) => {
  const msg = JSON.parse(e.data);
  if (pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
  if (msg.method === 'Runtime.exceptionThrown') errors.push(msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text);
  if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') errors.push(msg.params.args.map((a) => a.value ?? a.description).join(' '));
});
const send = (method, params = {}) => new Promise((r) => { const n = ++id; pending.set(n, r); ws.send(JSON.stringify({ id: n, method, params })); });
const ev = async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true })).result?.result?.value;
const snapshot = `(() => ({
  status: document.querySelector("[data-corner='hint']")?.textContent ?? null,
  labels: [...document.querySelectorAll('#Labels text')].filter((t) => t.style.display !== 'none').map((t) => t.textContent),
  errors: [...document.querySelectorAll('#ErrorRows li, .error-row li')].map((li) => li.textContent),
}))()`;

try {
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Page.navigate', { url: 'http://localhost:5259/simulation/' });
  await sleep(1500);
  const start = Date.now();
  let reloaded = false;
  while (Date.now() - start < Number(seconds) * 1000) {
    const ms = Date.now() - start;
    if (reloadAt && !reloaded && ms >= Number(reloadAt) * 1000) {
      reloaded = true;
      await send('Page.reload');
      await sleep(1500);
      appendFileSync(join(outDir, 'frames.jsonl'), JSON.stringify({ ms, reload: true }) + '\n');
      continue;
    }
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(outDir, `frame-${String(ms).padStart(6, '0')}.png`), Buffer.from(shot.result.data, 'base64'));
    appendFileSync(join(outDir, 'frames.jsonl'), JSON.stringify({ ms, ...(await ev(snapshot)) }) + '\n');
    await sleep(interval);
  }
} finally {
  writeFileSync(join(outDir, 'errors.json'), JSON.stringify(errors, null, 2));
  ws.close();
  proc.kill();
}
process.exit(0);
