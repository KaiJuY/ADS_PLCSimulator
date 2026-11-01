const browser = Object.fromEntries(['document', 'window', 'location', 'WebSocket', 'requestAnimationFrame', 'performance',
  'setTimeout', 'matchMedia', 'console', 'getComputedStyle']
  .map((name) => [name, 'readonly']));

export default [{
  files: ['**/*.js'],
  languageOptions: { ecmaVersion: 2024, sourceType: 'script', globals: browser },
  rules: { 'no-undef': 'error' },
}];
