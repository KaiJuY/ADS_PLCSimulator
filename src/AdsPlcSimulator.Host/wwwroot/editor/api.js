// Shared DOM lookup and JSON fetch helper used by every editor module.

export const $ = (id) => document.getElementById(id);

export async function api(path, options = {}) {
  const response = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(body?.error ?? `${response.status} ${response.statusText}`);
  return body;
}
