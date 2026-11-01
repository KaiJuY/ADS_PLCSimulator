// Drives the Editor in headless Edge over CDP (proposal 11 §6.3) and writes each step's result to a JSON file.
// Usage: node cdp11.mjs <repo-dir> <out.json> <screenshot-prefix>
import { spawn, execSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [repo, outFile, shotPrefix] = process.argv.slice(2);
const host = 'http://localhost:5259';
const probe = join(repo, 'projects', 'Polar2Chamber', 'workflows', 'Split11Probe.flow.json');
const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const profile = mkdtempSync(join(tmpdir(), 'edge11-'));
const proc = spawn(edge, ['--headless=new', '--disable-gpu', '--remote-debugging-port=9333', `--user-data-dir=${profile}`, '--window-size=1400,900', 'about:blank'], { stdio: 'ignore' });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let targets;
for (let i = 0; i < 50 && !targets; i++) {
  try { targets = await (await fetch('http://127.0.0.1:9333/json/list')).json(); } catch { await sleep(200); }
}
const page = targets.find((t) => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r));
let id = 0;
const pending = new Map();
const errors = [];
ws.addEventListener('message', (e) => {
  const msg = JSON.parse(e.data);
  if (pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
  if (msg.method === 'Page.javascriptDialogOpening') {
    ws.send(JSON.stringify({ id: ++id, method: 'Page.handleJavaScriptDialog', params: { accept: true, promptText: 'Split11Probe' } }));
  }
  if (msg.method === 'Runtime.exceptionThrown') errors.push(`exception: ${msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text}`);
  if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') errors.push(`console: ${msg.params.args.map((a) => a.value ?? a.description).join(' ')}`);
});
const send = (method, params = {}) => new Promise((r) => { const n = ++id; pending.set(n, r); ws.send(JSON.stringify({ id: n, method, params })); });
const evaluate = async (expression) => {
  const reply = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (reply.result?.exceptionDetails) throw new Error(`${expression}: ${reply.result.exceptionDetails.exception?.description}`);
  return reply.result?.result?.value;
};
const waitFor = async (expression, timeout = 5000) => {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await evaluate(expression)) return true; await sleep(100); }
  return false;
};
const dom = () => evaluate(`(() => {
  const layer = document.getElementById('layer').cloneNode(true);
  layer.style.removeProperty('transform');
  return { layer: layer.outerHTML, wires: document.getElementById('wires').outerHTML };
})()`);
const navigate = async (url) => { await send('Page.navigate', { url }); await sleep(1500); };

const result = {};
try {
  await send('Page.enable');
  await send('Runtime.enable');

  // 1. Stopped
  await navigate(`${host}/editor/`);
  await sleep(1000);
  const shot = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(`${shotPrefix}-stopped.png`, Buffer.from(shot.result.data, 'base64'));
  result.stopped = { ...(await dom()), palette: await evaluate("document.getElementById('paletteList').textContent"), state: await evaluate("document.getElementById('projectState').textContent") };

  // 2. Running
  await fetch(`${host}/api/project/run`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'Polar2Chamber' }) });
  await navigate(`${host}/editor/`);
  result.liveOpened = await waitFor("document.getElementById('liveState').textContent === 'live'");
  result.running = { ...(await dom()), workflows: await evaluate("[...document.getElementById('workflowSelect').options].map((o) => o.value)") };

  // 3. Open Polar2_Chamber_SetMode
  const open = "(() => { const s = document.getElementById('workflowSelect'); s.value = 'Polar2_Chamber_SetMode'; s.dispatchEvent(new Event('change')); })()";
  await evaluate(open);
  await sleep(1500);
  result.setMode = await dom();

  // 4. New Split11Probe (the prompt is answered by the dialog handler)
  await evaluate("document.getElementById('newButton').click()");
  await sleep(500);
  result.newWorkflow = { ...(await dom()), selected: await evaluate("document.getElementById('workflowSelect').value"), saveState: await evaluate("document.getElementById('saveState').textContent") };

  // 5. Quick add On Start and Delay
  const quickAdd = async (x, y, text) => {
    await evaluate(`document.getElementById('canvas').dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: ${x}, clientY: ${y} }))`);
    await sleep(200);
    await evaluate(`(() => { const s = document.getElementById('quickAddSearch'); s.value = ${JSON.stringify(text)}; s.dispatchEvent(new Event('input')); s.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    await sleep(300);
  };
  const canvasBox = await evaluate("(() => { const r = document.getElementById('canvas').getBoundingClientRect(); return { x: r.left, y: r.top }; })()");
  await quickAdd(canvasBox.x + 300, canvasBox.y + 200, 'On Start');
  await quickAdd(canvasBox.x + 700, canvasBox.y + 200, 'Delay');
  result.quickAdd = await dom();

  // 6. Drag a wire from onStart1.then to delay1.exec (nextNodeId keeps the last type segment: onStart1, delay1)
  const center = (node, dir, port) => evaluate(`(() => { const h = document.querySelector('.node[data-id="${node}"] .handle[data-dir="${dir}"][data-port="${port}"]'); const r = h.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  const from = await center('onStart1', 'out', 'then');
  const to = await center('delay1', 'in', 'exec');
  result.dragPoints = { from, to };
  await evaluate(`(() => {
    const h = document.elementFromPoint(${from.x}, ${from.y});
    h.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: ${from.x}, clientY: ${from.y} }));
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: ${to.x}, clientY: ${to.y} }));
    window.dispatchEvent(new PointerEvent('pointerup', { clientX: ${to.x}, clientY: ${to.y} }));
  })()`);
  await sleep(500);
  result.linked = await dom();

  // 7. Ctrl+S
  await evaluate("window.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }))");
  await waitFor("document.getElementById('saveState').textContent.startsWith('saved')");
  const saved = existsSync(probe) ? JSON.parse(readFileSync(probe, 'utf8')) : null;
  result.saved = saved && { nodes: saved.nodes.map((n) => `${n.id}:${n.type}`), links: saved.links };

  // 8. Live decoration from Integration S10
  await evaluate(open);
  await sleep(1500);
  execSync('dotnet run --project tests/AdsPlcSimulator.Integration -- --port 25860 --only S10', { cwd: repo, stdio: 'ignore' });
  result.runCompleted = await waitFor("document.querySelector('#runLog li.RunCompleted') !== null", 10000);
  await sleep(1000);
  result.hasRun = await evaluate("[...document.querySelectorAll('#layer .node.has-run')].map((n) => n.dataset.id).sort()");
} finally {
  result.errors = errors;
  writeFileSync(outFile, JSON.stringify(result, null, 2));
  ws.close();
  proc.kill();
}
process.exit(0);
