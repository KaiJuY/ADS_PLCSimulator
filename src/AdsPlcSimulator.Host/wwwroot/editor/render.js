import { state, els, CATEGORY_COLORS, setDirty } from './state.js';
import { findNode, definitionOf, isLinked } from './model.js';
import { decorateNode } from './live.js';
import { applyView, screenToWorld } from './view.js';
import { suggestSymbols } from './variable.js';

export function renderAll() {
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

export function placeGroupElement(element, group) {
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

export function portCenter(nodeId, dir, port) {
  const handle = els.layer.querySelector(`.node[data-id="${CSS.escape(nodeId)}"] .handle[data-dir="${dir}"][data-port="${CSS.escape(port)}"]`);
  if (!handle) return null;
  const rect = handle.getBoundingClientRect();
  return screenToWorld(rect.left + rect.width / 2, rect.top + rect.height / 2);
}

export function wirePath(start, end) {
  const bend = Math.max(40, Math.abs(end.x - start.x) / 2);
  return `M ${start.x} ${start.y} C ${start.x + bend} ${start.y}, ${end.x - bend} ${end.y}, ${end.x} ${end.y}`;
}

export function drawWires() {
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
