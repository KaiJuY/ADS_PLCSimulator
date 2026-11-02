import { els } from './state.js';

export function showTab(name) {
  const node = name === 'node';
  els.variableTab.classList.toggle('active', !node);
  els.nodeTab.classList.toggle('active', node);
  els.variableTab.setAttribute('aria-selected', String(!node));
  els.nodeTab.setAttribute('aria-selected', String(node));
  els.variablePane.hidden = node;
  els.nodePane.hidden = !node;
}

els.variableTab.addEventListener('click', () => showTab('variable'));
els.nodeTab.addEventListener('click', () => showTab('node'));
