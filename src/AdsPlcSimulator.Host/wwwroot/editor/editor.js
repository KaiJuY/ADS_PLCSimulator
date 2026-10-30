// Node-graph editor for simulator workflows. The workflow document ({nodes, links}) is the single source of
// truth; the DOM is re-rendered from it, and live runtime events only decorate the rendered nodes.

import { initProjectBar } from './project.js';

const CATEGORY_COLORS = {
  Trigger: '#e8743b',
  Flow: '#8b5cf6',
  Data: '#14b8a6',
  Logic: '#22c55e',
  Math: '#f59e0b',
  Action: '#3b82f6',
  Simulation: '#ec4899',
};
const WORKFLOW_NAME = /^[A-Za-z0-9_-]{1,64}$/;
const MAX_LOG_ENTRIES = 300;
const NEW_NODE_WIDTH = 220;
const NEW_GROUP_SIZE = { width: 420, height: 260 };
const MIN_GROUP_SIZE = { width: 160, height: 80 };

const state = {
  catalog: new Map(),
  name: null,
  doc: { nodes: [], links: [], groups: [] },
  clipboard: null,
  pointer: { x: 0, y: 0 },
  dirty: false,
  view: { x: 60, y: 40, scale: 1 },
  selection: null,
  diagnostics: [],
  live: new Map(),
};

const $ = (id) => document.getElementById(id);
const els = {
  canvas: $('canvas'),
  layer: $('layer'),
  wires: $('wires'),
  workflowSelect: $('workflowSelect'),
  saveState: $('saveState'),
  liveState: $('liveState'),
  paletteList: $('paletteList'),
  paletteSearch: $('paletteSearch'),
  problemList: $('problemList'),
  problemCount: $('problemCount'),
  runLog: $('runLog'),
  quickAdd: $('quickAdd'),
  quickAddActions: $('quickAddActions'),
  pasteAction: $('pasteAction'),
  quickAddSearch: $('quickAddSearch'),
  quickAddList: $('quickAddList'),
  symbolOptions: $('symbolOptions'),
  variableForm: $('variableForm'),
  variablePath: $('variablePath'),
  variableType: $('variableType'),
  variableValue: $('variableValue'),
  variableInput: $('variableInput'),
  variableError: $('variableError'),
};

// ---------- API ----------

async function api(path, options = {}) {
  const response = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(body?.error ?? `${response.status} ${response.statusText}`);
  return body;
}

// ---------- Workflow lifecycle ----------

async function refreshWorkflowList(selectName) {
  const workflows = await api('/api/workflows');
  els.workflowSelect.replaceChildren(...workflows.map((workflow) => {
    const option = new Option(`${workflow.name}${workflow.loaded ? '' : '  ⚠'}`, workflow.name);
    return option;
  }));
  if (selectName) els.workflowSelect.value = selectName;
  return workflows;
}

async function openWorkflow(name) {
  if (state.dirty && !confirm(`Discard unsaved changes to '${state.name}'?`)) {
    els.workflowSelect.value = state.name;
    return;
  }
  const doc = await api(`/api/workflows/${encodeURIComponent(name)}`);
  const workflows = await api('/api/workflows');
  state.name = name;
  els.workflowSelect.value = name;
  state.doc = { nodes: doc.nodes ?? [], links: doc.links ?? [], groups: doc.groups ?? [] };
  state.diagnostics = workflows.find((workflow) => workflow.name === name)?.diagnostics ?? [];
  state.selection = null;
  state.live.clear();
  setDirty(false);
  renderAll();
}

function newWorkflow() {
  const name = prompt('New workflow name (letters, digits, _ and -):', 'Chamber1_NewWorkflow');
  if (!name) return;
  if (!WORKFLOW_NAME.test(name)) {
    alert('Use only letters, digits, _ and - (max 64 characters).');
    return;
  }
  if ([...els.workflowSelect.options].some((option) => option.value === name)) {
    alert(`'${name}' already exists.`);
    return;
  }
  state.name = name;
  state.doc = { nodes: [], links: [], groups: [] };
  state.diagnostics = [];
  state.live.clear();
  els.workflowSelect.append(new Option(name, name));
  els.workflowSelect.value = name;
  setDirty(true);
  renderAll();
}

async function saveWorkflow() {
  if (document.body.classList.contains('project-stopped')) return;
  if (!state.name) return;
  try {
    const status = await api(`/api/workflows/${encodeURIComponent(state.name)}`, {
      method: 'PUT',
      body: JSON.stringify(state.doc),
    });
    state.diagnostics = status.diagnostics;
    setDirty(false);
    await refreshWorkflowList(state.name);
    renderAll();
  } catch (error) {
    setSaveState(`save failed: ${error.message}`, 'error');
  }
}

function setDirty(dirty) {
  state.dirty = dirty;
  if (dirty) return setSaveState('unsaved', 'warn');
  if (state.diagnostics.length) return setSaveState(`${state.diagnostics.length} problem(s), not running`, 'error');
  setSaveState('saved · running', 'ok');
}

function setSaveState(text, kind) {
  els.saveState.textContent = text;
  els.saveState.className = `pill ${kind}`;
}

// ---------- Model helpers ----------

const findNode = (id) => state.doc.nodes.find((node) => node.id === id);
const findGroup = (id) => state.doc.groups.find((group) => group.id === id);
const isInsideGroup = (node, group) =>
  node.x >= group.x && node.x < group.x + group.width && node.y >= group.y && node.y < group.y + group.height;
const definitionOf = (node) => state.catalog.get(node.type);
const linksTo = (nodeId, port) => state.doc.links.filter((link) => link.toNode === nodeId && link.toPort === port);
const isLinked = (dir, nodeId, port) => state.doc.links.some((link) =>
  dir === 'in' ? link.toNode === nodeId && link.toPort === port : link.fromNode === nodeId && link.fromPort === port);

function nextNodeId(type) {
  const prefix = type.split('.').pop().replace(/[^A-Za-z]/g, '');
  let index = 1;
  while (findNode(`${prefix}${index}`)) index++;
  return `${prefix}${index}`;
}

function addNode(type, x, y, params = {}) {
  const node = { id: nextNodeId(type), type, x: Math.round(x), y: Math.round(y), params: { ...params } };
  state.doc.nodes.push(node);
  state.selection = { kind: 'node', id: node.id };
  setDirty(true);
  renderAll();
  return node;
}

function addGroup(x, y) {
  let index = 1;
  while (findGroup(`group${index}`)) index++;
  const group = { id: `group${index}`, title: 'Group', x: Math.round(x), y: Math.round(y), ...NEW_GROUP_SIZE };
  state.doc.groups.push(group);
  state.selection = { kind: 'group', id: group.id };
  setDirty(true);
  renderAll();
}

function renameGroup(groupId) {
  const group = findGroup(groupId);
  const title = prompt('Group title:', group.title);
  if (title === null || title.trim() === group.title) return;
  group.title = title.trim();
  setDirty(true);
  renderAll();
}

function copySelection() {
  if (state.selection?.kind !== 'node') return;
  const node = findNode(state.selection.id);
  state.clipboard = { type: node.type, params: { ...node.params } };
}

function paste(point) {
  if (!state.clipboard) return;
  addNode(state.clipboard.type, point.x, point.y, state.clipboard.params);
}

// The first port on a new node that can take a wire dragged from `anchor` (same kind, opposite direction).
function compatiblePort(definition, anchor) {
  const ports = anchor.dir === 'out' ? definition.inputs : definition.outputs;
  return ports.find((port) => port.kind === anchor.kind);
}

function addLink(from, to) {
  const output = definitionOf(findNode(from.node))?.outputs.find((port) => port.name === from.port);
  const input = definitionOf(findNode(to.node))?.inputs.find((port) => port.name === to.port);
  if (!output || !input || output.kind !== input.kind || from.node === to.node) return;

  // A data input takes exactly one value, so a new link replaces the old one.
  if (input.kind === 'Data') {
    state.doc.links = state.doc.links.filter((link) => !(link.toNode === to.node && link.toPort === to.port));
  }
  const exists = state.doc.links.some((link) =>
    link.fromNode === from.node && link.fromPort === from.port && link.toNode === to.node && link.toPort === to.port);
  if (!exists) state.doc.links.push({ fromNode: from.node, fromPort: from.port, toNode: to.node, toPort: to.port });
  setDirty(true);
  renderAll();
}

function deleteSelection() {
  const selection = state.selection;
  if (!selection) return;
  if (selection.kind === 'node') {
    state.doc.nodes = state.doc.nodes.filter((node) => node.id !== selection.id);
    state.doc.links = state.doc.links.filter((link) => link.fromNode !== selection.id && link.toNode !== selection.id);
  } else if (selection.kind === 'group') {
    // Deleting a group removes only the frame; its nodes stay.
    state.doc.groups = state.doc.groups.filter((group) => group.id !== selection.id);
  } else {
    state.doc.links.splice(selection.index, 1);
  }
  state.selection = null;
  setDirty(true);
  renderAll();
}

// ---------- Rendering ----------

function renderAll() {
  els.layer.querySelectorAll('.node, .group').forEach((element) => element.remove());
  // Groups go first in the layer so wires and nodes paint over them.
  els.layer.prepend(...state.doc.groups.map(createGroupElement));
  for (const node of state.doc.nodes) els.layer.append(createNodeElement(node));
  applyView();
  renderProblems();
  requestAnimationFrame(drawWires);
}

function createNodeElement(node) {
  const definition = definitionOf(node);
  const element = document.createElement('div');
  element.className = 'node';
  element.dataset.id = node.id;
  element.style.left = `${node.x}px`;
  element.style.top = `${node.y}px`;
  element.style.setProperty('--category', CATEGORY_COLORS[definition?.category] ?? '#666');
  if (state.selection?.kind === 'node' && state.selection.id === node.id) element.classList.add('selected');

  const header = document.createElement('div');
  header.className = 'node-header';
  header.title = definition?.description ?? `Unknown node type ${node.type}`;
  header.innerHTML = '<span class="status-dot"></span><span class="title"></span><span class="node-id"></span>';
  header.querySelector('.title').textContent = definition?.title ?? node.type;
  header.querySelector('.node-id').textContent = node.id;
  element.append(header);

  const body = document.createElement('div');
  body.className = 'node-body';
  const inputs = definition?.inputs ?? [];
  const outputs = definition?.outputs ?? [];
  for (let row = 0; row < Math.max(inputs.length, outputs.length); row++) {
    const rowElement = document.createElement('div');
    rowElement.className = 'port-row';
    if (inputs[row]) rowElement.append(createPort(node, inputs[row], 'in'));
    if (outputs[row]) rowElement.append(createPort(node, outputs[row], 'out'));
    body.append(rowElement);
  }
  element.append(body);

  if (definition?.params.length) element.append(createParams(node, definition));

  const error = document.createElement('div');
  error.className = 'node-error';
  element.append(error);

  decorateNode(element, node.id);
  return element;
}

function createGroupElement(group) {
  const element = document.createElement('div');
  element.className = 'group';
  element.dataset.id = group.id;
  placeGroupElement(element, group);
  if (state.selection?.kind === 'group' && state.selection.id === group.id) element.classList.add('selected');
  const header = document.createElement('div');
  header.className = 'group-header';
  header.textContent = group.title;
  header.title = 'Drag to move with its nodes · double-click to rename';
  const resize = document.createElement('div');
  resize.className = 'group-resize';
  element.append(header, resize);
  return element;
}

function placeGroupElement(element, group) {
  element.style.left = `${group.x}px`;
  element.style.top = `${group.y}px`;
  element.style.width = `${group.width}px`;
  element.style.height = `${group.height}px`;
}

function createPort(node, port, dir) {
  const element = document.createElement('span');
  element.className = `port ${dir}`;
  const handle = document.createElement('span');
  handle.className = `handle ${port.kind.toLowerCase()}${isLinked(dir, node.id, port.name) ? ' linked' : ''}`;
  handle.dataset.node = node.id;
  handle.dataset.port = port.name;
  handle.dataset.dir = dir;
  handle.dataset.kind = port.kind;
  const label = document.createElement('span');
  label.className = 'label';
  label.textContent = port.label;
  element.append(handle, label);
  if (dir === 'out' && port.kind === 'Data') {
    const value = document.createElement('span');
    value.className = 'value';
    value.dataset.port = port.name;
    value.hidden = true;
    element.append(value);
  }
  return element;
}

function createParams(node, definition) {
  const container = document.createElement('div');
  container.className = 'params';
  for (const param of definition.params) {
    const row = document.createElement('div');
    row.className = 'param';
    const linkedInput = definition.inputs.some((port) => port.name === param.name) && isLinked('in', node.id, param.name);
    if (linkedInput) {
      row.classList.add('overridden');
      row.title = 'The linked input provides this value.';
    }

    const label = document.createElement('label');
    label.textContent = param.label;
    const control = createParamControl(node, param);
    control.disabled = linkedInput;
    row.append(label, control);
    container.append(row);
  }
  return container;
}

function createParamControl(node, param) {
  const current = node.params[param.name] ?? '';
  let control;
  if (param.kind === 'Choice') {
    control = document.createElement('select');
    for (const choice of param.choices ?? []) control.append(new Option(choice, choice));
    control.value = current || param.defaultValue;
  } else {
    control = document.createElement('input');
    control.type = param.kind === 'Number' ? 'number' : 'text';
    control.value = current;
    control.placeholder = param.defaultValue || (param.kind === 'Symbol' ? 'GVL.Variable.Member' : '');
    if (param.kind === 'Symbol') {
      control.classList.add('symbol');
      control.setAttribute('list', 'symbolOptions');
      control.addEventListener('focus', () => suggestSymbols(control.value));
      control.addEventListener('input', () => suggestSymbols(control.value));
    }
  }
  control.addEventListener('input', () => {
    node.params[param.name] = control.value;
    setDirty(true);
  });
  // Keep typing and clicking inside widgets from dragging or deleting the node.
  control.addEventListener('pointerdown', (event) => event.stopPropagation());
  control.addEventListener('keydown', (event) => event.stopPropagation());
  return control;
}

let symbolTimer;
function suggestSymbols(query) {
  clearTimeout(symbolTimer);
  symbolTimer = setTimeout(async () => {
    const symbols = await api(`/api/symbols?limit=80&query=${encodeURIComponent(query)}`);
    els.symbolOptions.replaceChildren(...symbols.map((symbol) => {
      const option = document.createElement('option');
      option.value = symbol.path;
      option.label = symbol.type;
      return option;
    }));
  }, 150);
}

function portCenter(nodeId, dir, port) {
  const handle = els.layer.querySelector(`.node[data-id="${CSS.escape(nodeId)}"] .handle[data-dir="${dir}"][data-port="${CSS.escape(port)}"]`);
  if (!handle) return null;
  const rect = handle.getBoundingClientRect();
  return screenToWorld(rect.left + rect.width / 2, rect.top + rect.height / 2);
}

function wirePath(start, end) {
  const bend = Math.max(40, Math.abs(end.x - start.x) / 2);
  return `M ${start.x} ${start.y} C ${start.x + bend} ${start.y}, ${end.x - bend} ${end.y}, ${end.x} ${end.y}`;
}

function drawWires() {
  els.wires.replaceChildren();
  state.doc.links.forEach((link, index) => {
    const start = portCenter(link.fromNode, 'out', link.fromPort);
    const end = portCenter(link.toNode, 'in', link.toPort);
    if (!start || !end) return;
    const kind = definitionOf(findNode(link.fromNode))?.outputs.find((port) => port.name === link.fromPort)?.kind ?? 'Data';
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', wirePath(start, end));
    path.setAttribute('class', `wire ${kind.toLowerCase()}`);
    path.dataset.index = index;
    path.dataset.from = link.fromNode;
    if (state.selection?.kind === 'link' && state.selection.index === index) path.classList.add('selected');
    path.addEventListener('pointerdown', (event) => {
      event.stopPropagation();
      state.selection = { kind: 'link', index };
      renderAll();
    });
    els.wires.append(path);
  });
}

function renderProblems() {
  els.problemCount.textContent = state.diagnostics.length;
  els.problemList.replaceChildren(...state.diagnostics.map((diagnostic) => {
    const item = document.createElement('li');
    item.innerHTML = '<b></b> <span></span>';
    item.querySelector('b').textContent = diagnostic.nodeId ?? 'workflow';
    item.querySelector('span').textContent = diagnostic.message;
    if (diagnostic.nodeId) item.addEventListener('click', () => focusNode(diagnostic.nodeId));
    return item;
  }));
}

function focusNode(nodeId) {
  const node = findNode(nodeId);
  if (!node) return;
  const rect = els.canvas.getBoundingClientRect();
  state.view.x = rect.width / 2 - node.x * state.view.scale - 100;
  state.view.y = rect.height / 2 - node.y * state.view.scale - 40;
  state.selection = { kind: 'node', id: nodeId };
  renderAll();
}

// ---------- Live runtime decoration ----------

function formatValue(value) {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : String(Number(value.toPrecision(6)));
  if (typeof value === 'string') return `'${value}'`;
  return JSON.stringify(value);
}

function decorateNode(element, nodeId) {
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

function connectLive(retryDelay = 500) {
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

// ---------- View, pan, zoom ----------

function applyView() {
  const { x, y, scale } = state.view;
  els.layer.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
  els.canvas.style.backgroundPosition = `${x}px ${y}px`;
  els.canvas.style.backgroundSize = `${22 * scale}px ${22 * scale}px`;
}

function screenToWorld(clientX, clientY) {
  const rect = els.canvas.getBoundingClientRect();
  return {
    x: (clientX - rect.left - state.view.x) / state.view.scale,
    y: (clientY - rect.top - state.view.y) / state.view.scale,
  };
}

// A group's body is canvas too: it pans, takes right-clicks and accepts dropped wires.
function isBackground(target) {
  return target === els.canvas || target === els.layer || target === els.wires || target?.classList.contains('group');
}

function startDrag(event, onMove, onEnd) {
  const move = (moveEvent) => onMove(moveEvent);
  const up = (upEvent) => {
    window.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', up);
    onEnd?.(upEvent);
  };
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', up);
}

els.canvas.addEventListener('pointerdown', (event) => {
  els.canvas.focus();
  hideQuickAdd();
  if (event.button !== 0) return;
  const handle = event.target.closest('.handle');
  if (handle) return startLinkDraft(event, handle);

  const header = event.target.closest('.node-header');
  if (header) return startNodeDrag(event, header.parentElement.dataset.id);

  const nodeElement = event.target.closest('.node');
  if (nodeElement) {
    select({ kind: 'node', id: nodeElement.dataset.id });
    return;
  }

  const groupHeader = event.target.closest('.group-header');
  if (groupHeader) return startGroupDrag(event, groupHeader.parentElement.dataset.id);

  const groupResize = event.target.closest('.group-resize');
  if (groupResize) return startGroupResize(event, groupResize.parentElement.dataset.id);

  if (!isBackground(event.target)) return;
  select(null);
  const origin = { x: event.clientX - state.view.x, y: event.clientY - state.view.y };
  els.canvas.classList.add('panning');
  startDrag(event, (move) => {
    state.view.x = move.clientX - origin.x;
    state.view.y = move.clientY - origin.y;
    applyView();
  }, () => els.canvas.classList.remove('panning'));
});

els.canvas.addEventListener('wheel', (event) => {
  event.preventDefault();
  const before = screenToWorld(event.clientX, event.clientY);
  state.view.scale = Math.min(2, Math.max(0.3, state.view.scale * (event.deltaY < 0 ? 1.1 : 0.9)));
  const rect = els.canvas.getBoundingClientRect();
  state.view.x = event.clientX - rect.left - before.x * state.view.scale;
  state.view.y = event.clientY - rect.top - before.y * state.view.scale;
  applyView();
}, { passive: false });

els.canvas.addEventListener('pointermove', (event) => {
  state.pointer = screenToWorld(event.clientX, event.clientY);
});

// Selects a node or group (null clears) without re-rendering, so a drag or double-click
// that starts on the same pointer press still finds its element.
function select(selection) {
  state.selection = selection;
  for (const element of els.layer.querySelectorAll('.node, .group')) {
    const kind = element.classList.contains('node') ? 'node' : 'group';
    element.classList.toggle('selected', selection?.kind === kind && selection.id === element.dataset.id);
  }
  for (const path of els.wires.querySelectorAll('.wire.selected')) path.classList.remove('selected');
}

function startGroupDrag(event, groupId) {
  select({ kind: 'group', id: groupId });
  const group = findGroup(groupId);
  const element = els.layer.querySelector(`.group[data-id="${CSS.escape(groupId)}"]`);
  const members = state.doc.nodes.filter((node) => isInsideGroup(node, group)).map((node) => ({
    node,
    origin: { x: node.x, y: node.y },
    element: els.layer.querySelector(`.node[data-id="${CSS.escape(node.id)}"]`),
  }));
  const start = screenToWorld(event.clientX, event.clientY);
  const origin = { x: group.x, y: group.y };
  startDrag(event, (move) => {
    const current = screenToWorld(move.clientX, move.clientY);
    const dx = Math.round(current.x - start.x);
    const dy = Math.round(current.y - start.y);
    group.x = origin.x + dx;
    group.y = origin.y + dy;
    placeGroupElement(element, group);
    for (const member of members) {
      member.node.x = member.origin.x + dx;
      member.node.y = member.origin.y + dy;
      member.element.style.left = `${member.node.x}px`;
      member.element.style.top = `${member.node.y}px`;
    }
    drawWires();
  }, () => {
    if (group.x !== origin.x || group.y !== origin.y) setDirty(true);
  });
}

function startGroupResize(event, groupId) {
  select({ kind: 'group', id: groupId });
  const group = findGroup(groupId);
  const element = els.layer.querySelector(`.group[data-id="${CSS.escape(groupId)}"]`);
  const start = screenToWorld(event.clientX, event.clientY);
  const origin = { width: group.width, height: group.height };
  startDrag(event, (move) => {
    const current = screenToWorld(move.clientX, move.clientY);
    group.width = Math.max(MIN_GROUP_SIZE.width, Math.round(origin.width + current.x - start.x));
    group.height = Math.max(MIN_GROUP_SIZE.height, Math.round(origin.height + current.y - start.y));
    placeGroupElement(element, group);
  }, () => {
    if (group.width !== origin.width || group.height !== origin.height) setDirty(true);
  });
}

function startNodeDrag(event, nodeId) {
  select({ kind: 'node', id: nodeId });
  const node = findNode(nodeId);
  const element = els.layer.querySelector(`.node[data-id="${CSS.escape(nodeId)}"]`);
  const start = screenToWorld(event.clientX, event.clientY);
  const origin = { x: node.x, y: node.y };
  startDrag(event, (move) => {
    const current = screenToWorld(move.clientX, move.clientY);
    node.x = Math.round(origin.x + current.x - start.x);
    node.y = Math.round(origin.y + current.y - start.y);
    element.style.left = `${node.x}px`;
    element.style.top = `${node.y}px`;
    drawWires();
  }, () => {
    if (node.x !== origin.x || node.y !== origin.y) setDirty(true);
  });
}

// Dragging from an input that already has a data link picks that link up, like ComfyUI.
function startLinkDraft(event, handle) {
  event.stopPropagation();
  let anchor = { node: handle.dataset.node, port: handle.dataset.port, dir: handle.dataset.dir, kind: handle.dataset.kind };
  if (anchor.dir === 'in' && anchor.kind === 'Data') {
    const existing = linksTo(anchor.node, anchor.port)[0];
    if (existing) {
      state.doc.links.splice(state.doc.links.indexOf(existing), 1);
      setDirty(true);
      renderAll();
      anchor = { node: existing.fromNode, port: existing.fromPort, dir: 'out', kind: 'Data' };
    }
  }

  const start = portCenter(anchor.node, anchor.dir, anchor.port);
  const draft = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  draft.setAttribute('class', `wire draft ${anchor.kind.toLowerCase()}`);
  els.wires.append(draft);

  startDrag(event, (move) => {
    const point = screenToWorld(move.clientX, move.clientY);
    draft.setAttribute('d', anchor.dir === 'out' ? wirePath(start, point) : wirePath(point, start));
  }, (up) => {
    draft.remove();
    const dropped = document.elementFromPoint(up.clientX, up.clientY);
    if (isBackground(dropped)) return showQuickAdd(up.clientX, up.clientY, { pending: anchor });
    const target = dropped?.closest('.handle');
    if (!target || target.dataset.dir === anchor.dir || target.dataset.kind !== anchor.kind) return;
    const other = { node: target.dataset.node, port: target.dataset.port };
    const own = { node: anchor.node, port: anchor.port };
    if (anchor.dir === 'out') addLink(own, other);
    else addLink(other, own);
  });
}

// ---------- Palette and quick add ----------

function filteredDefinitions(query) {
  const text = query.trim().toLowerCase();
  return [...state.catalog.values()].filter((definition) =>
    !text || `${definition.title} ${definition.category} ${definition.type}`.toLowerCase().includes(text));
}

function renderPalette() {
  const definitions = filteredDefinitions(els.paletteSearch.value);
  const groups = Map.groupBy(definitions, (definition) => definition.category);
  els.paletteList.replaceChildren();
  for (const [category, items] of groups) {
    const heading = document.createElement('h3');
    heading.textContent = category;
    els.paletteList.append(heading);
    for (const definition of items) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'palette-item';
      button.textContent = definition.title;
      button.title = definition.description;
      button.draggable = true;
      button.style.setProperty('--category', CATEGORY_COLORS[category] ?? '#666');
      button.addEventListener('click', () => {
        const rect = els.canvas.getBoundingClientRect();
        const center = screenToWorld(rect.left + rect.width / 2, rect.top + rect.height / 2);
        addNode(definition.type, center.x - 100, center.y - 40);
      });
      button.addEventListener('dragstart', (event) => event.dataTransfer.setData('text/node-type', definition.type));
      els.paletteList.append(button);
    }
  }
}

els.canvas.addEventListener('dragover', (event) => event.preventDefault());
els.canvas.addEventListener('drop', (event) => {
  const type = event.dataTransfer.getData('text/node-type');
  if (!type) return;
  event.preventDefault();
  const point = screenToWorld(event.clientX, event.clientY);
  addNode(type, point.x, point.y);
});

// One menu serves double-click, right-click and a wire dropped on empty canvas. A dropped wire
// leaves a pending anchor: the menu then lists only nodes that can take it, and links the new node.
const quickAdd = { point: null, index: 0, pending: null };

function showQuickAdd(clientX, clientY, { pending = null, actions = false } = {}) {
  quickAdd.point = screenToWorld(clientX, clientY);
  quickAdd.pending = pending;
  quickAdd.index = 0;
  els.quickAddActions.hidden = !actions;
  els.pasteAction.disabled = !state.clipboard;
  els.quickAddSearch.value = '';
  els.quickAdd.hidden = false;
  renderQuickAdd();
  // Keep the menu inside the window when opened near the bottom or right edge.
  const { width, height } = els.quickAdd.getBoundingClientRect();
  els.quickAdd.style.left = `${Math.min(clientX, window.innerWidth - width - 8)}px`;
  els.quickAdd.style.top = `${Math.min(clientY, window.innerHeight - height - 8)}px`;
  els.quickAddSearch.focus();
}

els.canvas.addEventListener('dblclick', (event) => {
  const groupHeader = event.target.closest('.group-header');
  if (groupHeader) return renameGroup(groupHeader.parentElement.dataset.id);
  if (isBackground(event.target)) showQuickAdd(event.clientX, event.clientY);
});

els.canvas.addEventListener('contextmenu', (event) => {
  event.preventDefault();
  if (isBackground(event.target)) showQuickAdd(event.clientX, event.clientY, { actions: true });
});

function quickAddDefinitions() {
  return filteredDefinitions(els.quickAddSearch.value)
    .filter((definition) => !quickAdd.pending || compatiblePort(definition, quickAdd.pending));
}

function renderQuickAdd() {
  const definitions = quickAddDefinitions();
  quickAdd.index = Math.min(quickAdd.index, Math.max(0, definitions.length - 1));
  els.quickAddList.replaceChildren(...definitions.map((definition, index) => {
    const item = document.createElement('li');
    item.style.setProperty('--category', CATEGORY_COLORS[definition.category] ?? '#666');
    item.innerHTML = '<span></span><small></small>';
    item.querySelector('span').textContent = definition.title;
    item.querySelector('small').textContent = definition.category;
    item.classList.toggle('active', index === quickAdd.index);
    item.addEventListener('pointerdown', (event) => {
      event.preventDefault();
      confirmQuickAdd(definition);
    });
    return item;
  }));
  return definitions;
}

function confirmQuickAdd(definition) {
  const { point, pending } = quickAdd;
  hideQuickAdd();
  if (!definition || !point) return;
  if (!pending) {
    addNode(definition.type, point.x, point.y);
    return;
  }

  // Place the node so its linked port sits near where the wire was dropped.
  const node = addNode(definition.type, pending.dir === 'in' ? point.x - NEW_NODE_WIDTH : point.x, point.y - 40);
  const port = { node: node.id, port: compatiblePort(definition, pending).name };
  const anchor = { node: pending.node, port: pending.port };
  if (pending.dir === 'out') addLink(anchor, port);
  else addLink(port, anchor);
}

function hideQuickAdd() {
  els.quickAdd.hidden = true;
  quickAdd.point = null;
  quickAdd.pending = null;
}

function runQuickAddAction(action) {
  const point = quickAdd.point;
  hideQuickAdd();
  if (point) action(point);
}

for (const button of els.quickAddActions.querySelectorAll('button')) {
  // Keep focus in the search box so its blur does not close the menu before the click lands.
  button.addEventListener('pointerdown', (event) => event.preventDefault());
}
$('newGroupAction').addEventListener('click', () => runQuickAddAction((point) => addGroup(point.x, point.y)));
els.pasteAction.addEventListener('click', () => runQuickAddAction(paste));

els.quickAddSearch.addEventListener('input', () => {
  quickAdd.index = 0;
  renderQuickAdd();
});
els.quickAddSearch.addEventListener('keydown', (event) => {
  const definitions = quickAddDefinitions();
  if (event.key === 'ArrowDown') quickAdd.index = Math.min(quickAdd.index + 1, definitions.length - 1);
  else if (event.key === 'ArrowUp') quickAdd.index = Math.max(quickAdd.index - 1, 0);
  else if (event.key === 'Enter') return confirmQuickAdd(definitions[quickAdd.index]);
  else if (event.key === 'Escape') return hideQuickAdd();
  else return;
  event.preventDefault();
  renderQuickAdd();
});
els.quickAddSearch.addEventListener('blur', () => setTimeout(hideQuickAdd, 100));

// ---------- PLC variable ----------

function showVariable(symbol) {
  els.variableType.textContent = symbol ? symbol.type : '';
  els.variableValue.textContent = symbol ? formatValue(symbol.value) : '';
  els.variableError.textContent = '';
}

let variableTimer;
async function readVariable() {
  const path = els.variablePath.value.trim();
  if (!path) return showVariable(null);
  try {
    showVariable(await api(`/api/symbols/value?path=${encodeURIComponent(path)}`));
  } catch (error) {
    showVariable(null);
    els.variableError.textContent = error.message;
  }
}

els.variablePath.addEventListener('focus', () => suggestSymbols(els.variablePath.value));
els.variablePath.addEventListener('input', () => {
  suggestSymbols(els.variablePath.value);
  clearTimeout(variableTimer);
  variableTimer = setTimeout(readVariable, 300);
});
els.variableForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    showVariable(await api('/api/symbols/value', {
      method: 'PUT',
      body: JSON.stringify({ path: els.variablePath.value.trim(), value: els.variableInput.value }),
    }));
  } catch (error) {
    els.variableError.textContent = error.message;
  }
});

// ---------- Keyboard and toolbar ----------

window.addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault();
    saveWorkflow();
    return;
  }
  const typing = ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName);
  if (typing) return;
  if (event.key === 'Delete' || event.key === 'Backspace') deleteSelection();
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c') copySelection();
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'v') paste(state.pointer);
});

window.addEventListener('beforeunload', (event) => {
  if (state.dirty) event.preventDefault();
});

$('saveButton').addEventListener('click', saveWorkflow);
$('newButton').addEventListener('click', newWorkflow);
$('clearLog').addEventListener('click', () => els.runLog.replaceChildren());
els.workflowSelect.addEventListener('change', () => openWorkflow(els.workflowSelect.value));
els.paletteSearch.addEventListener('input', renderPalette);
window.addEventListener('resize', () => requestAnimationFrame(drawWires));

// ---------- Startup ----------

async function init() {
  const definitions = await api('/api/nodes');
  for (const definition of definitions) state.catalog.set(definition.type, definition);
  renderPalette();
  const project = await initProjectBar({ isDirty: () => state.dirty });
  if (project.state !== 'Running') return;

  const workflows = await refreshWorkflowList();
  const requested = new URLSearchParams(location.search).get('workflow');
  const initial = workflows.find((workflow) => workflow.name === requested) ?? workflows[0];
  if (initial) await openWorkflow(initial.name);
  else newWorkflow();
  connectLive();
}

init().catch((error) => setSaveState(`startup failed: ${error.message}`, 'error'));
