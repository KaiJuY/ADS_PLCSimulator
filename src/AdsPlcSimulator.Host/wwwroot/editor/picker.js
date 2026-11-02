import { els } from './state.js';
import { createSymbolTree } from './tree.js';
import { suggestSymbols } from './variable.js';

let tree = null;

function updateOk() {
  els.symbolDialogOk.disabled = !els.symbolDialogPath.value.trim();
}

function setup() {
  tree = createSymbolTree({
    onPick: (path) => {
      els.symbolDialogPath.value = path;
      updateOk();
    },
    onError: (message) => { els.symbolDialogError.textContent = message; },
  });
  els.symbolDialogTree.append(tree.element);
  for (const type of ['input', 'focus']) {
    els.symbolDialogPath.addEventListener(type, () => {
      suggestSymbols(els.symbolDialogPath.value);
      updateOk();
    });
  }
  els.symbolDialogOk.addEventListener('click', () => els.symbolDialog.close('ok'));
  els.symbolDialogCancel.addEventListener('click', () => els.symbolDialog.close(''));
}

export function pickSymbol(initialPath) {
  if (!tree) setup();
  els.symbolDialogPath.value = initialPath;
  els.symbolDialogError.textContent = '';
  tree.clearSelection();
  tree.load();
  updateOk();
  els.symbolDialog.returnValue = '';
  return new Promise((resolve) => {
    els.symbolDialog.addEventListener('close', () => {
      resolve(els.symbolDialog.returnValue === 'ok' ? els.symbolDialogPath.value.trim() : null);
    }, { once: true });
    els.symbolDialog.showModal();
  });
}
