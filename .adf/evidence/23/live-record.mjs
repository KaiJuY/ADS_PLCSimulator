// Records every /live message as one JSON line with a receive time. Usage: node live-record.mjs <out.jsonl> [seconds]
// Without seconds it runs until Ctrl+C.
import { appendFileSync, writeFileSync } from 'node:fs';

const [outFile, seconds] = process.argv.slice(2);
writeFileSync(outFile, '');
const socket = new WebSocket('ws://localhost:5259/live');
const started = Date.now();
socket.addEventListener('message', (event) => {
  appendFileSync(outFile, JSON.stringify({ ms: Date.now() - started, message: JSON.parse(event.data) }) + '\n');
});
socket.addEventListener('close', () => process.exit(0));
if (seconds) setTimeout(() => { socket.close(); process.exit(0); }, Number(seconds) * 1000);
