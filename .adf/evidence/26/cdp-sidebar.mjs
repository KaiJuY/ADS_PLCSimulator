// Drives the Editor sidebar of #26 in headless Edge over CDP (proposal 26 §6.5, checks 1-12).
// Usage: node cdp-sidebar.mjs <repo-dir> <out.json> <screenshot-prefix>. The Host runs Polar2Chamber on :5259.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [, outFile, shotPrefix] = process.argv.slice(2);
const host = 'http://localhost:5259';
const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const profile = mkdtempSync(join(tmpdir(), 'edge26-'));
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
    ws.send(JSON.stringify({ id: ++id, method: 'Page.handleJavaScriptDialog', params: { accept: true, promptText: 'Split26Probe' } }));
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
const navigate = async (url) => { await send('Page.navigate', { url }); await sleep(1500); };
let shotIndex = 0;
const shot = async (label) => {
  const reply = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(`${shotPrefix}-${String(++shotIndex).padStart(2, '0')}-${label}.png`, Buffer.from(reply.result.data, 'base64'));
};
const mouse = (type, x, y) => send('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons: type === 'mouseReleased' ? 0 : 1, clickCount: 1 });
const key = async (keyName, code, keyCode, modifiers = 0) => {
  await send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: keyName, code, windowsVirtualKeyCode: keyCode, modifiers });
  await send('Input.dispatchKeyEvent', { type: 'keyUp', key: keyName, code, windowsVirtualKeyCode: keyCode, modifiers });
};
const typeInto = async (selector, text) => {
  await evaluate(`(() => { const e = document.querySelector(${JSON.stringify(selector)}); e.focus(); e.select(); })()`);
  await send('Input.insertText', { text });
  await sleep(100);
};
const pointerSelect = (selector) => evaluate(`(() => {
  const e = document.querySelector(${JSON.stringify(selector)}); const r = e.getBoundingClientRect();
  e.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: r.left + 5, clientY: r.top + r.height / 2 }));
  window.dispatchEvent(new PointerEvent('pointerup', { clientX: r.left + 5, clientY: r.top + r.height / 2 }));
})()`);
const tabs = () => evaluate("({ variable: document.getElementById('variableTab').classList.contains('active'), node: document.getElementById('nodeTab').classList.contains('active'), nodePaneHidden: document.getElementById('nodePane').hidden })");
const sidebarWidth = () => evaluate("Math.round(document.querySelector('.sidebar').getBoundingClientRect().width)");
const treeRow = (scope, path, from) => `[...document.querySelectorAll('${scope} button.tree-row')].find((b) => b.dataset.path === ${JSON.stringify(path)} && b.dataset.from === ${from === undefined ? 'undefined' : JSON.stringify(String(from))})`;
const expandRow = async (scope, path, from) => {
  await evaluate(`${treeRow(scope, path, from)}.click()`);
  return waitFor(`${treeRow(scope, path, from)}?.parentElement.dataset.loaded === '1'`);
};
const childRows = (scope, path, from) => evaluate(`[...${treeRow(scope, path, from)}.parentElement.querySelectorAll(':scope > ul > li > button.tree-row')].map((b) => ({ path: b.dataset.path, name: b.querySelector('.name').textContent, scalar: b.querySelector('.twisty').textContent === '', from: b.dataset.from }))`);

const W = '#layer .node[data-id="writeSymbol1"]';
const result = {};
try {
  await send('Page.enable');
  await send('Runtime.enable');
  // --window-size is not always applied in headless mode; the checks need a desktop-size viewport.
  await send('Emulation.setDeviceMetricsOverride', { width: 1374, height: 807, deviceScaleFactor: 1, mobile: false });
  await navigate(`${host}/editor/`);
  await waitFor("document.getElementById('liveState').textContent === 'live'");

  // 1. Initial tab state
  result.c1_initial = await tabs();
  await shot('initial');

  // Probe workflow: Read Symbol -> Write Symbol (the prompt is answered by the dialog handler)
  await evaluate("document.getElementById('newButton').click()");
  await sleep(500);
  const quickAdd = async (x, y, text) => {
    await evaluate(`document.getElementById('canvas').dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: ${x}, clientY: ${y} }))`);
    await sleep(200);
    await evaluate(`(() => { const s = document.getElementById('quickAddSearch'); s.value = ${JSON.stringify(text)}; s.dispatchEvent(new Event('input')); s.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()`);
    await sleep(300);
  };
  const canvasBox = await evaluate("(() => { const r = document.getElementById('canvas').getBoundingClientRect(); return { x: r.left, y: r.top }; })()");
  await quickAdd(canvasBox.x + 150, canvasBox.y + 150, 'Read Symbol');
  await quickAdd(canvasBox.x + 650, canvasBox.y + 150, 'Write Symbol');
  result.probeNodes = await evaluate("[...document.querySelectorAll('#layer .node')].map((n) => n.dataset.id)");

  // 2. Selecting a node switches to the Node tab
  await evaluate("document.getElementById('variableTab').click()");
  await pointerSelect(`${W} .node-body`);
  await sleep(200);
  result.c2_select = { tabs: await tabs(), id: await evaluate("document.querySelector('#nodeDetail .detail-id')?.textContent"), params: await evaluate("document.querySelectorAll('#nodeDetail .detail-param').length") };
  await shot('node-tab');

  // 3. Manual switch back, then re-select the same node
  await evaluate("document.getElementById('variableTab').click()");
  const back = await tabs();
  await pointerSelect(`${W} .node-body`);
  await sleep(200);
  result.c3_reselect = { back, again: await tabs() };

  // 4. Two-way sync, focus kept
  await typeInto('#nodeDetail [data-param="value"]', 'abc');
  result.c4_toCanvas = await evaluate(`({ canvas: document.querySelector('${W} [data-param="value"]').value, focusInDetail: document.getElementById('nodeDetail').contains(document.activeElement) })`);
  await typeInto(`${W} [data-param="value"]`, 'xyz');
  result.c4_toDetail = await evaluate("document.querySelector('#nodeDetail [data-param=\"value\"]').value");
  // focus() on a canvas field that is partly off screen scrolls #canvas (existing behavior, not #26); undo it so wires line up.
  await evaluate("document.getElementById('canvas').scrollLeft = 0; document.getElementById('canvas').scrollTop = 0");

  // 5. Ctrl+S and Delete inside a Node tab field
  await evaluate("document.querySelector('#nodeDetail [data-param=\"value\"]').focus()");
  await key('s', 'KeyS', 83, 2);
  result.c5_saved = await waitFor("document.getElementById('saveState').textContent !== 'unsaved'");
  result.c5_saveState = await evaluate("document.getElementById('saveState').textContent");
  await evaluate("document.querySelector('#nodeDetail [data-param=\"value\"]').focus()");
  await key('Delete', 'Delete', 46);
  await key('Backspace', 'Backspace', 8);
  result.c5_nodeKept = await evaluate(`document.querySelector('${W}') !== null`);

  // 6. Linked parameter: wire readSymbol1.value -> writeSymbol1.value
  const center = (node, dir, port) => evaluate(`(() => { const h = document.querySelector('.node[data-id="${node}"] .handle[data-dir="${dir}"][data-port="${port}"]'); const r = h.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()`);
  // The view pans between runs; bring the source port to a fixed spot so the drop point is on the canvas.
  const before = await center('readSymbol1', 'out', 'value');
  await evaluate(`(() => { const c = document.getElementById('canvas'); const r = c.getBoundingClientRect(); const sx = r.right - 40, sy = r.bottom - 60;
    c.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: sx, clientY: sy }));
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: sx + (r.left + 300 - ${before.x}), clientY: sy + (r.top + 200 - ${before.y}) }));
    window.dispatchEvent(new PointerEvent('pointerup', { clientX: sx + (r.left + 300 - ${before.x}), clientY: sy + (r.top + 200 - ${before.y}) })); })()`);
  await sleep(200);
  const from = await center('readSymbol1', 'out', 'value');
  const to = await center('writeSymbol1', 'in', 'value');
  await evaluate(`(() => {
    const h = document.querySelector('.node[data-id="readSymbol1"] .handle[data-dir="out"][data-port="value"]');
    h.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: ${from.x}, clientY: ${from.y} }));
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: ${to.x}, clientY: ${to.y} }));
    window.dispatchEvent(new PointerEvent('pointerup', { clientX: ${to.x}, clientY: ${to.y} }));
  })()`);
  await sleep(300);
  await pointerSelect(`${W} .node-body`);
  await sleep(200);
  result.c6_linked = await evaluate("(() => { const row = document.querySelector('#nodeDetail .detail-param.linked'); return row && { text: row.querySelector('.detail-linked').textContent, hasInput: row.querySelector('input, select') !== null }; })()");
  result.c6_wire = await evaluate("(() => { const p = document.querySelector('#wires path.wire'); const b = p?.getBoundingClientRect(); return p && { d: p.getAttribute('d'), box: [b.left, b.top, b.width, b.height].map(Math.round) }; })()");
  await sleep(300);
  await shot('linked');

  // 7. Empty canvas and wire selection show the hint
  await evaluate(`(() => { const c = document.getElementById('canvas'); const r = c.getBoundingClientRect();
    c.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: r.right - 40, clientY: r.bottom - 60 }));
    window.dispatchEvent(new PointerEvent('pointerup', { clientX: r.right - 40, clientY: r.bottom - 60 })); })()`);
  await sleep(200);
  result.c7_empty = await evaluate("document.querySelector('#nodeDetail .detail-empty')?.textContent ?? null");
  await pointerSelect(`${W} .node-body`);
  await evaluate("document.querySelector('#wires path.wire').dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0 }))");
  await sleep(200);
  result.c7_wire = await evaluate("document.querySelector('#nodeDetail .detail-empty')?.textContent ?? null");

  // 8. Resize with real mouse input (pointer capture needs an active pointer)
  const handle = await evaluate("(() => { const r = document.getElementById('sidebarResize').getBoundingClientRect(); const l = document.getElementById('layout').getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2, right: l.right }; })()");
  const drag = async (x) => { await mouse('mousePressed', handle.x, handle.y); await mouse('mouseMoved', x, handle.y); await mouse('mouseReleased', x, handle.y); await sleep(200); handle.x = x; };
  await drag(handle.right - 450 - 3);
  result.c8_450 = await sidebarWidth();
  await shot('width-450');
  await drag(handle.right - 900);
  result.c8_max = await sidebarWidth();
  await navigate(`${host}/editor/?workflow=Split26Probe`);
  result.c8_reload = await sidebarWidth();
  await evaluate("localStorage.setItem('adsplc-editor:sidebarWidth', 'abc')");
  await navigate(`${host}/editor/?workflow=Split26Probe`);
  result.c8_invalid = await sidebarWidth();

  // 9. Symbol tree in the PLC Variable tab
  await evaluate("document.getElementById('variableTab').click()");
  await waitFor("document.querySelector('#variableTree button.tree-row') !== null");
  result.c9_root = await evaluate("[...document.querySelectorAll('#variableTree > ul > li > button.tree-row')].map((b) => b.dataset.path)");
  const base = 'GVL_Module.Chamber_Control[1].ModuleConfigParamList';
  result.c9_expand = [];
  for (const path of ['GVL_Module', 'GVL_Module.Chamber_Control', 'GVL_Module.Chamber_Control[1]', base]) result.c9_expand.push(await expandRow('#variableTree', path));
  result.c9_ranges = (await childRows('#variableTree', base)).map((r) => r.name);
  result.c9_rangeLoaded = await expandRow('#variableTree', base, 1);
  result.c9_rangeCount = (await childRows('#variableTree', base, 1)).length;
  await expandRow('#variableTree', `${base}[1]`);
  const member = (await childRows('#variableTree', `${base}[1]`)).find((r) => r.scalar);
  await evaluate(`${treeRow('#variableTree', member.path)}.click()`);
  await waitFor("document.getElementById('variableValue').textContent !== ''");
  result.c9_pick = await evaluate("({ path: document.getElementById('variablePath').value, value: document.getElementById('variableValue').textContent, error: document.getElementById('variableError').textContent })");
  result.c9_member = member.path;
  await shot('tree');

  // 10. Picker dialog: OK, Cancel, Escape
  await pointerSelect(`${W} .node-body`);
  await sleep(200);
  await evaluate("document.querySelector('#nodeDetail .detail-param .pick').click()");
  await sleep(200);
  result.c10_open = await evaluate("document.getElementById('symbolDialog').open");
  result.c10_okDisabledEmpty = await evaluate("document.getElementById('symbolDialogOk').disabled");
  await waitFor("document.querySelector('#symbolDialogTree button.tree-row') !== null");
  for (const path of ['GVL_Module', 'GVL_Module.Chamber_Control', 'GVL_Module.Chamber_Control[1]', base]) await expandRow('#symbolDialogTree', path);
  await expandRow('#symbolDialogTree', base, 1);
  await expandRow('#symbolDialogTree', `${base}[1]`);
  await evaluate(`${treeRow('#symbolDialogTree', member.path)}.click()`);
  result.c10_okEnabledAfterPick = await evaluate("!document.getElementById('symbolDialogOk').disabled");
  await shot('dialog');
  await evaluate("document.getElementById('symbolDialogOk').click()");
  await sleep(200);
  const symbolValues = `({ detail: document.querySelector('#nodeDetail [data-param="symbol"]').value, canvas: document.querySelector('${W} [data-param="symbol"]').value, open: document.getElementById('symbolDialog').open })`;
  result.c10_afterOk = await evaluate(symbolValues);
  const other = (await childRows('#symbolDialogTree', `${base}[1]`)).filter((r) => r.scalar)[1].path;
  await evaluate("document.querySelector('#nodeDetail .detail-param .pick').click()");
  await sleep(200);
  await evaluate(`${treeRow('#symbolDialogTree', other)}.click()`);
  await evaluate("document.getElementById('symbolDialogCancel').click()");
  await sleep(200);
  result.c10_afterCancel = await evaluate(symbolValues);
  await evaluate("document.querySelector('#nodeDetail .detail-param .pick').click()");
  await sleep(200);
  await evaluate(`${treeRow('#symbolDialogTree', other)}.click()`);
  await key('Escape', 'Escape', 27);
  await sleep(200);
  result.c10_afterEscape = await evaluate(symbolValues);

  // 11. Errors after the project stops (this page is not reloaded)
  await fetch(`${host}/api/project/stop`, { method: 'POST' });
  await sleep(500);
  await evaluate("document.getElementById('variableTab').click()");
  await evaluate(`${treeRow('#variableTree', base, 51)}.click()`);
  await waitFor("document.getElementById('variableTreeError').textContent !== ''");
  result.c11_treeError = await evaluate("document.getElementById('variableTreeError').textContent");
  await evaluate("(() => { const p = document.getElementById('variablePath'); p.value = 'GVL_Module'; p.dispatchEvent(new Event('input')); })()");
  await sleep(800);
  await shot('stopped-error');
} catch (error) {
  result.failure = error.message;
} finally {
  result.errors = errors;
  writeFileSync(outFile, JSON.stringify(result, null, 2));
  ws.close();
  proc.kill();
}
process.exit(0);
