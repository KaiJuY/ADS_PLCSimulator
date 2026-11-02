import { state, els } from './state.js';
import { findNode, findGroup, definitionOf, linksTo, isInsideGroup } from './lookup.js';
import { createParamControl, isLinkedParam } from './render.js';
import { showTab } from './tabs.js';
import { formatValue } from './variable.js';

const EMPTY_TEXT = 'Select a node on the canvas';
let shownNodeId = null;

function element(tag, className, text) {
  const result = document.createElement(tag);
  if (className) result.className = className;
  if (text !== undefined) result.textContent = text;
  return result;
}

function headElement(title, type, id) {
  const head = element('div', 'detail-head');
  head.append(element('b', 'detail-title', title));
  if (type) head.append(' ', element('span', 'detail-type', type));
  if (id) head.append(' ', element('code', 'detail-id', id));
  return head;
}

function paramRow(node, definition, param) {
  const row = element('div', 'detail-param');
  row.append(element('label', '', param.label));
  if (isLinkedParam(node, definition, param)) {
    const link = linksTo(node.id, param.name)[0];
    row.classList.add('linked');
    row.append(element('div', 'detail-linked', `← ${link.fromNode}.${link.fromPort}`));
    return row;
  }
  const control = element('div', 'detail-control');
  control.append(createParamControl(node, param));
  row.append(control);
  return row;
}

function paramsElement(node, definition) {
  const params = element('div', 'detail-params');
  if (!definition) {
    for (const [key, value] of Object.entries(node.params)) params.append(element('div', 'detail-readonly', `${key} = ${value}`));
    return params;
  }
  for (const param of definition.params) params.append(paramRow(node, definition, param));
  return params;
}

function runElement(node, definition) {
  const live = state.live.get(node.id);
  const run = element('div', 'detail-run');
  run.append(element('h3', '', 'Run'), element('div', 'detail-status', live?.status ?? 'not run'));
  const outputs = element('dl', 'detail-outputs');
  for (const port of (definition?.outputs ?? []).filter((item) => item.kind === 'Data')) {
    const has = Object.hasOwn(live?.outputs ?? {}, port.name);
    outputs.append(element('dt', '', port.label), element('dd', '', has ? formatValue(live.outputs[port.name]) : '—'));
  }
  run.append(outputs);
  return run;
}

function errorsElement(node) {
  const messages = state.diagnostics.filter((item) => item.nodeId === node.id).map((item) => item.message);
  const live = state.live.get(node.id);
  if (live?.status === 'failed') messages.push(live.error);
  const errors = element('div', 'detail-errors');
  errors.hidden = messages.length === 0;
  const list = element('ul');
  for (const message of messages) list.append(element('li', '', message));
  errors.append(element('h3', '', 'Errors'), list);
  return errors;
}

function showNode(node) {
  const definition = definitionOf(node);
  const keepParams = node.id === shownNodeId && els.nodeDetail.querySelector('.detail-params')?.contains(document.activeElement);
  if (!keepParams) {
    els.nodeDetail.replaceChildren(headElement(definition?.title ?? node.type, node.type, node.id), paramsElement(node, definition), runElement(node, definition), errorsElement(node));
    return;
  }
  els.nodeDetail.querySelector('.detail-head').replaceWith(headElement(definition?.title ?? node.type, node.type, node.id));
  els.nodeDetail.querySelector('.detail-run').replaceWith(runElement(node, definition));
  els.nodeDetail.querySelector('.detail-errors').replaceWith(errorsElement(node));
}

function showGroup(group) {
  const count = state.doc.nodes.filter((node) => isInsideGroup(node, group)).length;
  els.nodeDetail.replaceChildren(headElement(group.title, `${count} nodes`));
}

export function renderDetail() {
  const selection = state.selection;
  const node = selection?.kind === 'node' ? findNode(selection.id) : null;
  const group = selection?.kind === 'group' ? findGroup(selection.id) : null;
  if (node && node.id !== shownNodeId) showTab('node');
  if (node) showNode(node);
  else if (group) showGroup(group);
  else els.nodeDetail.replaceChildren(element('p', 'detail-empty', EMPTY_TEXT));
  shownNodeId = node?.id ?? null;
}

export function refreshDetailLive(nodeId) {
  if (shownNodeId === null || (nodeId !== undefined && nodeId !== shownNodeId)) return;
  const node = findNode(shownNodeId);
  if (!node) return;
  const definition = definitionOf(node);
  els.nodeDetail.querySelector('.detail-run').replaceWith(runElement(node, definition));
  els.nodeDetail.querySelector('.detail-errors').replaceWith(errorsElement(node));
}

document.addEventListener('editor:rendered', renderDetail);
