// Usage: node cycles.mjs <editor folder>. Lists elementary import cycles; reads single-line imports only (also matches import text inside comments).
import fs from 'node:fs';
import path from 'node:path';

const dir = process.argv[2];
const graph = new Map();
const importPattern = /^\s*import\s+(?:[^'"]*?\s+from\s+)?'\.\/([^']+)\.js'/;
for (const file of fs.readdirSync(dir).filter((name) => name.endsWith('.js'))) {
  const targets = [];
  for (const line of fs.readFileSync(path.join(dir, file), 'utf8').split(/\r?\n/)) {
    const match = importPattern.exec(line);
    if (match) targets.push(match[1]);
  }
  graph.set(file.slice(0, -3), targets);
}

const cycles = [];
for (const start of graph.keys()) {
  const walk = (node, trail) => {
    for (const next of graph.get(node) ?? []) {
      if (next === start) cycles.push([...trail, start].join(' -> '));
      else if (next > start && !trail.includes(next)) walk(next, [...trail, next]);
    }
  };
  walk(start, [start]);
}

const unique = [...new Set(cycles)].sort();
for (const cycle of unique) console.log(cycle);
console.log(`cycles: ${unique.length}`);
process.exitCode = unique.length > 0 ? 1 : 0;
