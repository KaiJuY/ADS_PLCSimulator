import { els, SIDEBAR_WIDTH, SIDEBAR_WIDTH_KEY } from './state.js';
import { drawWires } from './render.js';

const HANDLE_HALF_WIDTH = 3;
let width = SIDEBAR_WIDTH.fallback;

function applyWidth(value) {
  width = Math.min(SIDEBAR_WIDTH.max, Math.max(SIDEBAR_WIDTH.min, value));
  els.layout.style.setProperty('--sidebar-width', `${width}px`);
}

function loadWidth() {
  let saved = null;
  try { saved = localStorage.getItem(SIDEBAR_WIDTH_KEY); } catch {}
  const value = Number(saved);
  applyWidth(saved === null || !Number.isFinite(value) ? SIDEBAR_WIDTH.fallback : value);
}

els.sidebarResize.addEventListener('pointerdown', (event) => {
  event.preventDefault();
  els.sidebarResize.setPointerCapture(event.pointerId);
});
els.sidebarResize.addEventListener('pointermove', (event) => {
  if (!els.sidebarResize.hasPointerCapture(event.pointerId)) return;
  applyWidth(els.layout.getBoundingClientRect().right - event.clientX - HANDLE_HALF_WIDTH);
});
els.sidebarResize.addEventListener('lostpointercapture', () => {
  try { localStorage.setItem(SIDEBAR_WIDTH_KEY, String(width)); } catch {}
  requestAnimationFrame(drawWires);
});

loadWidth();
