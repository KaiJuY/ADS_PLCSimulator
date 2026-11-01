// Drives headless Edge over CDP: cross-site stop page, then the Editor's Stop and Run buttons.
import { spawn } from 'node:child_process';

const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const profile = process.env.TEMP + '\\edge9cdp';
const proc = spawn(edge, ['--headless=new', '--disable-gpu', '--remote-debugging-port=9333', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const state = async () => (await (await fetch('http://localhost:5259/api/project')).json()).state;

let targets;
for (let i = 0; i < 50 && !targets; i++) {
  try { targets = await (await fetch('http://127.0.0.1:9333/json/list')).json(); } catch { await sleep(200); }
}
const page = targets.find((t) => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r));
let id = 0;
const pending = new Map();
ws.addEventListener('message', (e) => {
  const msg = JSON.parse(e.data);
  if (pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
});
const send = (method, params = {}) => new Promise((r) => { const n = ++id; pending.set(n, r); ws.send(JSON.stringify({ id: n, method, params })); });
const evaluate = async (expression) => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })).result?.result?.value;

const results = [];
const check = (name, expected, actual) => { results.push(`${expected === actual ? 'PASS' : 'FAIL'} ${name}: expected ${expected}, actual ${actual}`); };

check('before', 'Running', await state());

await send('Page.navigate', { url: 'http://localhost:8765/stop.html' });
await sleep(2500);
check('cross-site page result text', 'sent', await evaluate("document.getElementById('r').textContent"));
check('state after cross-site stop', 'Running', await state());

await send('Page.navigate', { url: 'http://localhost:5259/editor/' });
await sleep(3000);
await evaluate("document.getElementById('stopButton').click()");
await sleep(3000);
check('state after Editor Stop', 'Stopped', await state());
check('Editor projectState', 'Stopped', await evaluate("document.getElementById('projectState').textContent"));

await evaluate("document.getElementById('projectSelect').value = 'Polar2Chamber'; document.getElementById('runButton').click()");
await sleep(4000);
check('state after Editor Run', 'Running', await state());
check('Editor error hidden', true, await evaluate("document.getElementById('projectError').hidden"));

console.log(results.join('\n'));
ws.close();
proc.kill();
process.exit(results.every((r) => r.startsWith('PASS')) ? 0 : 1);
