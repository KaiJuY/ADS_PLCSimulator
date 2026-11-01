// Loads the real editor module graph in Node with browser stubs.
// Usage: node load-graph.mjs <dir with editor .js files>
// Success: every named import exists, evaluation order and TDZ are fine, top-level code touches no uninitialized binding.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const source = path.resolve(process.argv[2] ?? '.');
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'load-graph-'));
for (const file of fs.readdirSync(source)) {
  if (file.endsWith('.js')) fs.copyFileSync(path.join(source, file), path.join(temp, file));
}
fs.writeFileSync(path.join(temp, 'package.json'), '{"type":"module"}');

const fakeElement = () => new Proxy(function () {}, {
  get(target, prop) {
    if (prop === Symbol.toPrimitive) return () => '';
    if (prop === 'then') return undefined;
    if (prop === 'options' || prop === 'children') return [];
    if (prop === 'classList' || prop === 'style' || prop === 'dataset') return fakeElement();
    if (prop === 'value' || prop === 'textContent' || prop === 'className' || prop === 'innerHTML') return '';
    return fakeElement();
  },
  set() { return true; },
  apply() { return fakeElement(); },
});
globalThis.document = {
  getElementById: () => fakeElement(),
  querySelector: () => fakeElement(),
  querySelectorAll: () => [],
  createElement: () => fakeElement(),
  createElementNS: () => fakeElement(),
  addEventListener() {},
  body: fakeElement(),
  activeElement: null,
};
globalThis.window = { addEventListener() {}, innerWidth: 1400, innerHeight: 900 };
globalThis.location = { search: '', protocol: 'http:', host: 'localhost', reload() {} };
globalThis.WebSocket = class { addEventListener() {} };
globalThis.fetch = () => new Promise(() => {});
globalThis.requestAnimationFrame = () => 0;
globalThis.Option = class { constructor(text, value) { this.text = text; this.value = value; } };
globalThis.CSS = { escape: (value) => String(value) };

const errors = [];
process.on('unhandledRejection', (reason) => errors.push(reason));
try {
  await import(pathToFileURL(path.join(temp, 'editor.js')).href);
  await new Promise((resolve) => setTimeout(resolve, 100));
  if (errors.length) throw errors[0];
  console.log(`OK: module graph loaded from ${source}`);
} catch (error) {
  console.log(`FAIL: ${error.name}: ${error.message}`);
  process.exitCode = 1;
} finally {
  fs.rmSync(temp, { recursive: true, force: true });
}
