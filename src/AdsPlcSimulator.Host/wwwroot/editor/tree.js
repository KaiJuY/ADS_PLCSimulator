import { api } from './api.js';

const TWISTY_CLOSED = '▸';
const TWISTY_OPEN = '▾';

function childrenUrl(button) {
  const range = button.dataset.from === undefined ? '' : `&from=${button.dataset.from}&to=${button.dataset.to}`;
  return `/api/symbols/children?path=${encodeURIComponent(button.dataset.path)}${range}`;
}

export function createSymbolTree({ onPick, onError }) {
  const element = document.createElement('ul');
  element.className = 'symbol-tree';

  function clearSelection() {
    for (const selected of element.querySelectorAll('.selected')) selected.classList.remove('selected');
  }

  function pick(button) {
    clearSelection();
    button.classList.add('selected');
    onPick(button.dataset.path);
  }

  async function expand(item, button) {
    if (button.getAttribute('aria-busy') === 'true') return;
    button.setAttribute('aria-busy', 'true');
    try {
      const children = await api(childrenUrl(button));
      item.append(createList(children));
      item.dataset.loaded = '1';
      button.querySelector('.twisty').textContent = TWISTY_OPEN;
      onError('');
    } catch (error) {
      onError(error.message);
    } finally {
      button.removeAttribute('aria-busy');
    }
  }

  function toggle(item, button) {
    if (!item.dataset.loaded) return expand(item, button);
    const list = item.querySelector(':scope > ul');
    list.hidden = !list.hidden;
    button.querySelector('.twisty').textContent = list.hidden ? TWISTY_CLOSED : TWISTY_OPEN;
  }

  function createItem(child) {
    const item = document.createElement('li');
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'tree-row';
    button.dataset.path = child.path;
    if (Number.isInteger(child.from)) {
      button.dataset.from = child.from;
      button.dataset.to = child.to;
    }
    const twisty = document.createElement('span');
    twisty.className = 'twisty';
    twisty.textContent = child.isScalar ? '' : TWISTY_CLOSED;
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = child.name;
    button.append(twisty, name);
    if (child.type !== null) {
      const type = document.createElement('span');
      type.className = 'type';
      type.textContent = child.type;
      button.append(type);
    }
    button.addEventListener('click', () => (child.isScalar ? pick(button) : toggle(item, button)));
    item.append(button);
    return item;
  }

  function createList(children) {
    const list = document.createElement('ul');
    if (children.length === 0) {
      const empty = document.createElement('li');
      empty.className = 'tree-empty';
      empty.textContent = '(empty)';
      list.append(empty);
      return list;
    }
    list.append(...children.map(createItem));
    return list;
  }

  function showRetry() {
    const item = document.createElement('li');
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'tree-row retry';
    button.textContent = 'Retry';
    button.addEventListener('click', load);
    item.append(button);
    element.replaceChildren(item);
  }

  async function load() {
    if (element.dataset.loaded || element.dataset.loading) return;
    element.dataset.loading = '1';
    try {
      const children = await api('/api/symbols/children');
      element.replaceChildren(...children.map(createItem));
      element.dataset.loaded = '1';
      onError('');
    } catch (error) {
      onError(error.message);
      showRetry();
    } finally {
      delete element.dataset.loading;
    }
  }

  return { element, load, clearSelection };
}
