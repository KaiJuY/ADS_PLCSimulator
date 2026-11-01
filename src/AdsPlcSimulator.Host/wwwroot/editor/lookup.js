import { state } from './state.js';

export const findNode = (id) => state.doc.nodes.find((node) => node.id === id);
export const findGroup = (id) => state.doc.groups.find((group) => group.id === id);
export const isInsideGroup = (node, group) =>
  node.x >= group.x && node.x < group.x + group.width && node.y >= group.y && node.y < group.y + group.height;
export const definitionOf = (node) => state.catalog.get(node.type);
export const linksTo = (nodeId, port) => state.doc.links.filter((link) => link.toNode === nodeId && link.toPort === port);
export const isLinked = (dir, nodeId, port) => state.doc.links.some((link) =>
  dir === 'in' ? link.toNode === nodeId && link.toPort === port : link.fromNode === nodeId && link.fromPort === port);
