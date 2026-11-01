// Editor constants, the shared mutable state and the cached DOM elements.

import { $ } from './api.js';

export const CATEGORY_COLORS = {
  Trigger: '#e8743b',
  Flow: '#8b5cf6',
  Data: '#14b8a6',
  Logic: '#22c55e',
  Math: '#f59e0b',
  Action: '#3b82f6',
  Simulation: '#ec4899',
};
export const WORKFLOW_NAME = /^[A-Za-z0-9_-]{1,64}$/;
export const MAX_LOG_ENTRIES = 300;
export const NEW_NODE_WIDTH = 220;
export const NEW_GROUP_SIZE = { width: 420, height: 260 };
export const MIN_GROUP_SIZE = { width: 160, height: 80 };

export const state = {
  catalog: new Map(),
  name: null,
  doc: { nodes: [], links: [], groups: [] },
  clipboard: null,
  pointer: { x: 0, y: 0 },
  dirty: false,
  view: { x: 60, y: 40, scale: 1 },
  selection: null,
  diagnostics: [],
  live: new Map(),
};

export const els = {
  canvas: $('canvas'),
  layer: $('layer'),
  wires: $('wires'),
  workflowSelect: $('workflowSelect'),
  saveState: $('saveState'),
  liveState: $('liveState'),
  paletteList: $('paletteList'),
  paletteSearch: $('paletteSearch'),
  problemList: $('problemList'),
  problemCount: $('problemCount'),
  runLog: $('runLog'),
  quickAdd: $('quickAdd'),
  quickAddActions: $('quickAddActions'),
  pasteAction: $('pasteAction'),
  quickAddSearch: $('quickAddSearch'),
  quickAddList: $('quickAddList'),
  symbolOptions: $('symbolOptions'),
  variableForm: $('variableForm'),
  variablePath: $('variablePath'),
  variableType: $('variableType'),
  variableValue: $('variableValue'),
  variableInput: $('variableInput'),
  variableError: $('variableError'),
};
