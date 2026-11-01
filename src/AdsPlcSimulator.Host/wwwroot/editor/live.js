import { state, els, MAX_LOG_ENTRIES, setDirty } from './state.js';
import { renderAll } from './render.js';
import { readVariable } from './variable.js';

export function formatValue(value) {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : String(Number(value.toPrecision(6)));
  if (typeof value === 'string') return `'${value}'`;
  return JSON.stringify(value);
}

export function decorateNode(element, nodeId) {
  const live = state.live.get(nodeId);
  const diagnostic = state.diagnostics.find((item) => item.nodeId === nodeId);
  element.classList.toggle('invalid', Boolean(diagnostic));
  element.classList.toggle('running', live?.status === 'running');
  element.classList.toggle('failed', live?.status === 'failed');
  element.classList.toggle('has-run', live?.status === 'done');
  const errorText = diagnostic?.message ?? (live?.status === 'failed' ? live.error : '');
  element.querySelector('.node-error').textContent = errorText ?? '';
  for (const valueElement of element.querySelectorAll('.value')) {
    const outputs = live?.outputs ?? {};
    const has = Object.prototype.hasOwnProperty.call(outputs, valueElement.dataset.port);
    valueElement.hidden = !has;
    if (has) {
      valueElement.textContent = formatValue(outputs[valueElement.dataset.port]);
      valueElement.title = valueElement.textContent;
    }
  }
}

function applyFlowEvent(event) {
  appendRunLog(event);
  if (event.workflow.toLowerCase() !== state.name?.toLowerCase() || !event.nodeId) return;

  const live = state.live.get(event.nodeId) ?? { outputs: {} };
  switch (event.kind) {
    case 'RunStarted':
      for (const [, entry] of state.live) if (entry.status === 'failed') entry.status = 'done';
      for (const element of els.layer.querySelectorAll('.node')) decorateNode(element, element.dataset.id);
      return;
    case 'NodeStarted':
      live.status = 'running';
      break;
    case 'NodeCompleted':
      live.status = 'done';
      live.outputs = { ...live.outputs, ...(event.outputs ?? {}) };
      pulseWires(event.nodeId);
      break;
    case 'NodeFailed':
      live.status = 'failed';
      live.error = event.error;
      break;
    default:
      return;
  }
  state.live.set(event.nodeId, live);

  const element = els.layer.querySelector(`.node[data-id="${CSS.escape(event.nodeId)}"]`);
  if (!element) return;
  decorateNode(element, event.nodeId);
  if (event.kind === 'NodeCompleted') {
    element.classList.remove('done');
    void element.offsetWidth;
    element.classList.add('done');
  }
}

function pulseWires(nodeId) {
  for (const path of els.wires.querySelectorAll(`.wire[data-from="${CSS.escape(nodeId)}"]`)) {
    path.classList.add('pulse');
    setTimeout(() => path.classList.remove('pulse'), 450);
  }
}

function appendRunLog(event) {
  const interesting = ['RunStarted', 'RunCompleted', 'RunFailed', 'RunSkipped', 'NodeFailed'];
  if (!interesting.includes(event.kind)) return;
  const item = document.createElement('li');
  item.className = event.kind;
  const time = new Date(event.timestamp).toLocaleTimeString([], { hour12: false });
  item.innerHTML = '<span class="time"></span><span class="text"></span>';
  item.querySelector('.time').textContent = time;
  const detail = event.error ? ` · ${event.error}` : '';
  item.querySelector('.text').textContent = `${event.workflow} #${event.runId} ${event.kind.replace('Run', 'run ').replace('Node', 'node ')}${event.nodeId ? ` @${event.nodeId}` : ''}${detail}`;
  addLogItem(item);
}

function appendSimulationLog(command) {
  const item = document.createElement('li');
  item.innerHTML = '<span class="time"></span><span class="text"></span>';
  item.querySelector('.time').textContent = new Date().toLocaleTimeString([], { hour12: false });
  item.querySelector('.text').textContent = `sim ${command.target} → ${command.action}${command.value === null ? '' : ` (${formatValue(command.value)})`}`;
  addLogItem(item);
}

function addLogItem(item) {
  els.runLog.prepend(item);
  while (els.runLog.children.length > MAX_LOG_ENTRIES) els.runLog.lastChild.remove();
}

export function connectLive(retryDelay = 500) {
  const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
  const socket = new WebSocket(`${protocol}://${location.host}/live`);
  socket.addEventListener('open', () => {
    els.liveState.textContent = 'live';
    els.liveState.className = 'pill ok';
    retryDelay = 500;
  });
  socket.addEventListener('message', (message) => {
    const data = JSON.parse(message.data);
    if (data.type === 'flow') applyFlowEvent(data.event);
    // A finished run may have changed the watched variable.
    if (data.type === 'flow' && ['RunCompleted', 'RunFailed'].includes(data.event.kind)) readVariable();
    if (data.type === 'sim') appendSimulationLog(data.command);
    if (data.type === 'workflow' && data.status.name === state.name && !state.dirty) {
      state.diagnostics = data.status.diagnostics;
      setDirty(false);
      renderAll();
    }
  });
  socket.addEventListener('close', () => {
    els.liveState.textContent = 'offline · reconnecting';
    els.liveState.className = 'pill error';
    setTimeout(() => connectLive(Math.min(retryDelay * 2, 8000)), retryDelay);
  });
}
