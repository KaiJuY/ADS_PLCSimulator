// Page self-test for the Polar2 Simulation page (#24).
// Loads projects/Polar2Chamber/simulation/index.html from file: in headless Edge, feeds sim commands to the page's own
// onMessage(...) through CDP, and checks state and DOM. Never opens a socket to the Host or to any AMS port.
// Usage: node selftest.mjs [--shots-only]
// Writes initial.png and all-on.png next to this file.
import { spawn } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const PAGE = resolve(HERE, '../../../projects/Polar2Chamber/simulation/index.html');
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const PORT = 9335;
const shotsOnly = process.argv.includes('--shots-only');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const profile = mkdtempSync(join(tmpdir(), 'edge24-'));
const proc = spawn(EDGE, ['--headless=new', '--disable-gpu', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`, '--window-size=1100,1000', 'about:blank'], { stdio: 'ignore' });

let targets;
for (let i = 0; i < 50 && !targets; i++) { try { targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json(); } catch { await sleep(200); } }
const page = targets.find((t) => t.type === 'page');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r));
let id = 0;
const pending = new Map();
const exceptions = [];
ws.addEventListener('message', (e) => {
  const msg = JSON.parse(e.data);
  if (pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); return; }
  if (msg.method === 'Runtime.exceptionThrown') exceptions.push(msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text);
  if (msg.method === 'Runtime.consoleAPICalled' && msg.params.type === 'error') exceptions.push(msg.params.args.map((a) => a.value ?? a.description).join(' '));
});
const send = (method, params = {}) => new Promise((r) => { const n = ++id; pending.set(n, r); ws.send(JSON.stringify({ id: n, method, params })); });
const ev = async (expression) => {
  const reply = await send('Runtime.evaluate', { expression, returnByValue: true });
  if (reply.result?.exceptionDetails) throw new Error(`evaluate failed: ${expression}\n${reply.result.exceptionDetails.exception?.description ?? reply.result.exceptionDetails.text}`);
  return reply.result?.result?.value;
};

const results = [];
const check = (name, ok, detail = '') => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`); };
const near = (a, b, tol) => Math.abs(a - b) <= tol;

const msg = (data) => ev(`onMessage(${JSON.stringify(data)})`);
const reset = (name = 'SelfTest') => msg({ type: 'project', name, running: true });
const cmd = (target, action, value, durationMs) => msg({ type: 'sim', command: { target, action, value, durationMs } });
const val = (expr) => ev(`valueAt(${expr}, performance.now())`);
const redraw = () => ev('draw(performance.now())');
const exists = (selector) => ev(`document.querySelector(${JSON.stringify(selector)}) !== null`);
const count = (selector) => ev(`document.querySelectorAll(${JSON.stringify(selector)}).length`);
const stored = (key) => ev(`window.localStorage.getItem('polar2-sim:SelfTest:${key}')`);
const shot = async (name) => {
  const box = await ev('(() => { const r = document.querySelector(".iso-plate").getBoundingClientRect(); return [r.x + scrollX, r.y + scrollY, r.width, r.height]; })()');
  const clip = { x: box[0], y: box[1], width: box[2], height: box[3], scale: 1.6 };
  const data = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, clip });
  writeFileSync(join(HERE, name), Buffer.from(data.result.data, 'base64'));
};

async function allOn() {
  await reset();
  const on = (target, value) => cmd(target, 'On', value, 0);
  for (let arm = 1; arm <= 4; arm++) for (let nozzle = 1; nozzle <= [3, 3, 2, 1][arm - 1]; nozzle++) await on(`Arm${arm}.Nozzle${nozzle}`, 20);
  for (let arm = 1; arm <= 2; arm++) for (let port = 1; port <= 3; port++) await on(`Arm${arm}.Port${port}`, 20);
  for (let n = 1; n <= 4; n++) {
    await on(`FixedNozzle${n}`, 20);
    await on(`ExhaustFan${n}`, 250);
    await cmd(`SideVent${n}`, 'Open', null, 500);
  }
  await cmd('Holder', 'CW', 600, 0);
  await cmd('OutsideRing', 'CCW', 60, 0);
  await cmd('Holder.Gripper', 'Close', null, 500);
  await cmd('OutsideRing.Lift', 'Top', null, 500);
  await cmd('Shutter', 'Open', null, 500);
  await cmd('Damper', 'SEX', null, 500);
  await cmd('Cup2.Master', 'Top', 40, 500);
  await cmd('Cup2.Slave', 'Top', 40, 500);
  await cmd('Cup1.Drain', 'Select2', null, 0);
  await cmd('Arm1.PortDrain', 'Select3', null, 0);
  await cmd('Arm2.Rotate', 'Noz1Center', 0, 500);
}

try {
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1100, height: 1000, deviceScaleFactor: 1.5, mobile: false });
  await send('Page.navigate', { url: pathToFileURL(PAGE).href });
  await sleep(1200);
  await ev('window.localStorage.clear()');

  await reset();
  await redraw();
  await sleep(300);
  await shot('initial.png');
  await allOn();
  await sleep(900);
  await redraw();
  await shot('all-on.png');
  if (shotsOnly) process.exit(0);

  // 1 initial shape
  await reset();
  await redraw();
  const wanted = ['Base', 'SideVent1', 'SideVent2', 'SideVent3', 'SideVent4', 'Fan1', 'Fan2', 'Fan3', 'Fan4', 'Damper', 'Shutter', 'Arm1-PortCup', 'Arm2-PortCup', 'Arm3-PortCup', 'Arm4-PortCup'];
  const missing = [];
  for (const name of wanted) if (!(await exists(`#${name}`))) missing.push(name);
  check('1 initial: no #FFU, all new groups exist', !(await exists('#FFU')) && missing.length === 0, missing.join(','));

  // 2 shutter
  await reset();
  await cmd('Shutter', 'Open', null, 1000);
  await sleep(1100);
  const open = await ev('[valueAt(state.shutter, performance.now()), SHUTTER_Z.Open, SHUTTER_Z.Close]');
  await cmd('Shutter', 'Close', null, 500);
  await sleep(600);
  const closed = await val('state.shutter');
  check('2 Shutter Open reaches SHUTTER_Z.Open, Close returns', near(open[0], open[1], 0.01) && near(closed, open[2], 0.01), `open ${open[0]}, close ${closed}`);

  // 3 jog positive, hold, localStorage, replay
  await reset();
  await cmd('Arm1.Rotate', 'NozzlePort', 22, 0);
  await cmd('Arm1.Rotate', 'JogPositive', 10, null);
  await sleep(2000);
  const moving = await val('state.rotate[0]');
  await cmd('Arm1.Rotate', 'Hold');
  const frozen = await val('state.rotate[0]');
  await sleep(500);
  const later = await val('state.rotate[0]');
  const saved = Number(await stored('Arm1.Rotate'));
  check('3a JogPositive 2 s from -35 reaches about -15', near(moving, -15, 2), `${moving.toFixed(2)}`);
  check('3b Hold freezes the value', near(frozen, later, 1e-9), `${frozen.toFixed(3)} / ${later.toFixed(3)}`);
  check('3c localStorage holds the frozen value', near(saved, frozen, 1e-9), `${saved}`);
  await reset();
  await msg({ type: 'simState', commands: [{ target: 'Arm1.Rotate', action: 'JogPositive', value: 10 }] });
  const replayed = await val('state.rotate[0]');
  check('3d simState replay of JogPositive restores the stored value', near(replayed, saved, 1e-9), `${replayed}`);

  // 4 jog negative to the limit
  await reset();
  await cmd('Arm1.Rotate', 'NozzlePort', 22, 0);
  await cmd('Arm1.Rotate', 'JogNegative', 10, null);
  await sleep(2000);
  const limit = await val('state.rotate[0]');
  const busyAtLimit = await ev('busy(performance.now())');
  await cmd('Arm1.Rotate', 'Hold');
  check('4 JogNegative stops at -48 and stays busy until Hold', near(limit, -48, 1e-9) && busyAtLimit === true, `${limit}, busy ${busyAtLimit}`);

  // 5 incremental jog
  const middle = async () => { await reset(); await cmd('Cup1.Master', 'Middle', null, 0); return val('state.cups[0].Master'); };
  const step = (value, durationMs = 500) => cmd('Cup1.Master', 'IncrementalJog', value, durationMs);
  const done = () => cmd('Cup1.Master', 'StepDone', null, 0);
  const cup = () => val('state.cups[0].Master');
  let base = await middle();
  await step(1); await sleep(600); await done();
  const plus = (await cup()) - base;
  await step(-1); await sleep(600); await done();
  const minus = (await cup()) - base;
  await step(0); await sleep(100); await done();
  const zero = (await cup()) - base;
  check('5a value 1 gives +4 mm, value -1 returns, value 0 does not move', near(plus, 4, 1e-9) && near(minus, 0, 1e-9) && near(zero, 0, 1e-9), `${plus}, ${minus}, ${zero}`);
  base = await middle();
  await step(1); await sleep(200); await cmd('Cup1.Master', 'Hold');
  await step(1); await done();
  const resumed = (await cup()) - base;
  check('5b Hold then the same step again, then StepDone, gives +4 mm in total', near(resumed, 4, 1e-9), `${resumed}`);
  base = await middle();
  await step(1); await sleep(480); await done();
  const early = (await cup()) - base;
  await step(1); await done();
  const second = (await cup()) - base;
  check('5c StepDone before the animation ends gives exactly +4, the next step +4 more', near(early, 4, 1e-9) && near(second, 8, 1e-9), `${early}, ${second}`);
  base = await middle();
  await step(1); await sleep(250); await cmd('Cup1.Master', 'Hold');
  const held = await cup();
  await step(-1); await done();
  const back = await cup();
  check('5d Hold then the opposite step moves 4 mm from the held position', held > base && near(back, held - 4, 1e-9), `${held}, ${back}`);

  // 6 holder home
  const turns = () => ev('shownAngle(state.spin.Holder, performance.now()) / (2 * Math.PI)');
  const whole = (n) => near(n, Math.round(n), 1e-9);
  await reset();
  await cmd('Holder', 'CW', 60, 0); await sleep(300);
  await cmd('Holder', 'Home', null, 500); await sleep(600);
  const first = await turns();
  await cmd('Holder', 'Home', null, 500); await sleep(600);
  const again = await turns();
  check('6a Home ends at a whole number of turns, a second Home keeps it', whole(first) && near(first, again, 1e-9), `${first}, ${again}`);
  await reset();
  await cmd('Holder', 'CW', 60, 0); await sleep(300);
  await cmd('Holder', 'Home', null, 1000); await sleep(300);
  await cmd('Holder', 'Hold');
  const held1 = await turns();
  await sleep(400);
  const held2 = await turns();
  check('6b Hold during Home stops the angle', near(held1, held2, 1e-9), `${held1}, ${held2}`);
  await reset();
  await cmd('Holder', 'CW', 60, 0); await sleep(300);
  await cmd('Holder', 'Home', null, 0);
  check('6c Home with durationMs 0 is a whole number of turns at once', whole(await turns()));

  // 9 damper and drain labels
  await reset();
  await cmd('Damper', 'SEX', null, 500); await sleep(600);
  await cmd('Cup1.Drain', 'Select2', null, 0);
  await redraw();
  const damper = await ev('[valueAt(state.damper, performance.now()), state.labels.Damper, state.labels["Cup1.Drain"]]');
  await reset();
  await msg({ type: 'simState', commands: [{ target: 'Cup1.Drain', action: 'Select2' }] });
  await redraw();
  const shown = await ev("[...document.querySelectorAll('#Labels text')].filter((t) => t.style.display !== 'none').map((t) => t.textContent)");
  check('9a Damper SEX gives position 1 and label SEX, Drain Select2 gives label Drain 2', damper[0] === 1 && damper[1] === 'SEX' && damper[2] === 'Drain 2', JSON.stringify(damper));
  check('9b Drain label is shown after a simState replay', shown.some((text) => text.includes('Drain 2')), JSON.stringify(shown));

  // 10 errors and exceptions
  const errorText = await ev('document.getElementById("errors").textContent');
  check('10a error rows are empty', errorText === '', errorText);
  check('10b no page exceptions', exceptions.length === 0, exceptions.join(' | '));
} finally {
  const failed = results.filter((r) => !r.ok).length;
  console.log(`\n${results.length - failed}/${results.length} checks passed, ${exceptions.length} page exceptions`);
  ws.close();
  proc.kill();
  process.exitCode = failed || exceptions.length ? 1 : 0;
}
process.exit(process.exitCode);
