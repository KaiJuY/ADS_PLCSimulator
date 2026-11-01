// Node-graph editor for simulator workflows. The workflow document ({nodes, links}) is the single source of
// truth; the DOM is re-rendered from it, and live runtime events only decorate the rendered nodes.

import { initProjectBar } from './project.js';
import { $, api } from './api.js';
import { state, els, WORKFLOW_NAME, setDirty, setSaveState } from './state.js';
import { copySelection, paste, deleteSelection } from './model.js';
import { renderAll, drawWires } from './render.js';
import { renderPalette } from './palette.js';
import { connectLive } from './live.js';
import './variable.js';
import './canvas.js';

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
