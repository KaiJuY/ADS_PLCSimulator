// Usage: node extract-script.mjs <index.html> <out.js>. Writes the content of the last <script> element to out.js.
// The inlined kit contains the texts "</main>" and "<script" in strings, so the element is found as "</main><script".
import { readFileSync, writeFileSync } from 'node:fs';

const [html, out] = process.argv.slice(2);
const text = readFileSync(html, 'utf8');
const start = text.lastIndexOf('</main><script');
const open = text.indexOf('>', start + '</main>'.length) + 1;
const close = text.lastIndexOf('</script>');
writeFileSync(out, text.slice(open, close));
