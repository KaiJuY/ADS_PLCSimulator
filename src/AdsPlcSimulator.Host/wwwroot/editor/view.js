import { state, els } from './state.js';

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
