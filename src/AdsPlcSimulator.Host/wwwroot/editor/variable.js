import { api } from './api.js';
import { els } from './state.js';

export function formatValue(value) {
  if (value === null || value === undefined) return 'null';
  if (typeof value === 'boolean') return value ? 'TRUE' : 'FALSE';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : String(Number(value.toPrecision(6)));
  if (typeof value === 'string') return `'${value}'`;
  return JSON.stringify(value);
}

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

let symbolTimer;
export function suggestSymbols(query) {
  clearTimeout(symbolTimer);
  symbolTimer = setTimeout(async () => {
    const symbols = await api(`/api/symbols?limit=80&query=${encodeURIComponent(query)}`);
    els.symbolOptions.replaceChildren(...symbols.map((symbol) => {
      const option = document.createElement('option');
      option.value = symbol.path;
      option.label = symbol.type;
      return option;
    }));
  }, 150);
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
