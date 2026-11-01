import { state, NEW_GROUP_SIZE } from './state.js';
import { setDirty } from './editor.js';
import { renderAll } from './render.js';

export const findNode = (id) => state.doc.nodes.find((node) => node.id === id);
export const findGroup = (id) => state.doc.groups.find((group) => group.id === id);
export const isInsideGroup = (node, group) =>
  node.x >= group.x && node.x < group.x + group.width && node.y >= group.y && node.y < group.y + group.height;
export const definitionOf = (node) => state.catalog.get(node.type);
export const linksTo = (nodeId, port) => state.doc.links.filter((link) => link.toNode === nodeId && link.toPort === port);
export const isLinked = (dir, nodeId, port) => state.doc.links.some((link) =>
  dir === 'in' ? link.toNode === nodeId && link.toPort === port : link.fromNode === nodeId && link.fromPort === port);

function nextNodeId(type) {
  const prefix = type.split('.').pop().replace(/[^A-Za-z]/g, '');
  let index = 1;
  while (findNode(`${prefix}${index}`)) index++;
  return `${prefix}${index}`;
}

export function addNode(type, x, y, params = {}) {
  const node = { id: nextNodeId(type), type, x: Math.round(x), y: Math.round(y), params: { ...params } };
  state.doc.nodes.push(node);
  state.selection = { kind: 'node', id: node.id };
  setDirty(true);
  renderAll();
  return node;
}

export function addGroup(x, y) {
  let index = 1;
  while (findGroup(`group${index}`)) index++;
  const group = { id: `group${index}`, title: 'Group', x: Math.round(x), y: Math.round(y), ...NEW_GROUP_SIZE };
  state.doc.groups.push(group);
  state.selection = { kind: 'group', id: group.id };
  setDirty(true);
  renderAll();
}

export function renameGroup(groupId) {
  const group = findGroup(groupId);
  const title = prompt('Group title:', group.title);
  if (title === null || title.trim() === group.title) return;
  group.title = title.trim();
  setDirty(true);
  renderAll();
}

export function copySelection() {
  if (state.selection?.kind !== 'node') return;
  const node = findNode(state.selection.id);
  state.clipboard = { type: node.type, params: { ...node.params } };
}

export function paste(point) {
  if (!state.clipboard) return;
  addNode(state.clipboard.type, point.x, point.y, state.clipboard.params);
}

// The first port on a new node that can take a wire dragged from `anchor` (same kind, opposite direction).
export function compatiblePort(definition, anchor) {
  const ports = anchor.dir === 'out' ? definition.inputs : definition.outputs;
  return ports.find((port) => port.kind === anchor.kind);
}

export function addLink(from, to) {
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

export function deleteSelection() {
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
