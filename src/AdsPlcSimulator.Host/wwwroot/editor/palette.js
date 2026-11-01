import { $ } from './api.js';
import { state, els, CATEGORY_COLORS, NEW_NODE_WIDTH } from './state.js';
import { addNode, addGroup, renameGroup, paste, compatiblePort, addLink } from './model.js';
import { screenToWorld, isBackground } from './canvas.js';

function filteredDefinitions(query) {
  const text = query.trim().toLowerCase();
  return [...state.catalog.values()].filter((definition) =>
    !text || `${definition.title} ${definition.category} ${definition.type}`.toLowerCase().includes(text));
}

export function renderPalette() {
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

export function showQuickAdd(clientX, clientY, { pending = null, actions = false } = {}) {
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

export function hideQuickAdd() {
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
