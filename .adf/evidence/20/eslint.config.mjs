const browser = Object.fromEntries(['document', 'window', 'location', 'fetch', 'WebSocket', 'CSS', 'Option',
  'requestAnimationFrame', 'prompt', 'alert', 'confirm', 'URLSearchParams', 'setTimeout', 'clearTimeout',
  'setInterval', 'clearInterval', 'console', 'localStorage', 'sessionStorage', 'history', 'navigator',
  'getComputedStyle', 'Event', 'KeyboardEvent', 'MouseEvent', 'PointerEvent', 'FormData', 'URL']
  .map((name) => [name, 'readonly']));

export default [{
  files: ['**/*.js'],
  languageOptions: { ecmaVersion: 2024, sourceType: 'module', globals: browser },
  rules: { 'no-undef': 'error', 'no-unused-vars': ['error', { args: 'none' }] },
}];
