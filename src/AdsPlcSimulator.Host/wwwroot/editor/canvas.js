// Canvas view: pan, zoom, selection and dragging of nodes, groups and link drafts.

import { state, els, MIN_GROUP_SIZE } from './state.js';
import { findNode, findGroup, isInsideGroup, linksTo, addLink } from './model.js';
import { setDirty } from './editor.js';
import { renderAll, placeGroupElement, drawWires, portCenter, wirePath } from './render.js';
import { showQuickAdd, hideQuickAdd } from './palette.js';

export function applyView() {
  const { x, y, scale } = state.view;
  els.layer.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
  els.canvas.style.backgroundPosition = `${x}px ${y}px`;
  els.canvas.style.backgroundSize = `${22 * scale}px ${22 * scale}px`;
}

export function screenToWorld(clientX, clientY) {
  const rect = els.canvas.getBoundingClientRect();
  return {
    x: (clientX - rect.left - state.view.x) / state.view.scale,
    y: (clientY - rect.top - state.view.y) / state.view.scale,
  };
}

// A group's body is canvas too: it pans, takes right-clicks and accepts dropped wires.
export function isBackground(target) {
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
