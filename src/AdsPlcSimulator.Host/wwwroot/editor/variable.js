// PLC variable reader: symbol lookup and typed value display.

import { api } from './api.js';
import { els } from './state.js';
import { suggestSymbols } from './render.js';
import { formatValue } from './live.js';

function showVariable(symbol) {
  els.variableType.textContent = symbol ? symbol.type : '';
  els.variableValue.textContent = symbol ? formatValue(symbol.value) : '';
  els.variableError.textContent = '';
}

let variableTimer;
export async function readVariable() {
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
