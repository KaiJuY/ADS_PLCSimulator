const $ = (id) => document.getElementById(id);
const reloadToAttachProject = () => location.reload();

async function api(path, options = {}) {
  const response = await fetch(path, { headers: { 'Content-Type': 'application/json' }, ...options });
  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(body?.error ?? `${response.status} ${response.statusText}`);
  return body;
}

function selectedProject(status) {
  if (status.running) return status.running;
  if (status.last && status.projects.includes(status.last)) return status.last;
  return status.projects[0];
}

function renderProjects(status) {
  const select = $('projectSelect');
  if (status.projects.length === 0) {
    select.replaceChildren(new Option('(no projects)', ''));
    select.options[0].disabled = true;
    return;
  }
  select.replaceChildren(...status.projects.map((name) => new Option(name, name)));
  select.value = selectedProject(status);
}

function renderState(status) {
  const running = status.state === 'Running';
  document.body.classList.toggle('project-stopped', !running);
  $('stoppedNotice').hidden = running;
  $('projectSelect').disabled = running || status.projects.length === 0;
  $('runButton').disabled = running || status.projects.length === 0;
  $('stopButton').disabled = !running;
  $('projectState').textContent = running ? `Running: ${status.running} (AMS ${status.port})` : 'Stopped';
}

function showError(message) {
  $('projectError').textContent = message;
  $('projectError').hidden = false;
}

async function run() {
  const button = $('runButton');
  button.disabled = true;
  $('projectError').hidden = true;
  try {
    await api('/api/project/run', { method: 'POST', body: JSON.stringify({ name: $('projectSelect').value }) });
    reloadToAttachProject();
  } catch (error) {
    showError(error.message);
    button.disabled = false;
  }
}

async function stop(isDirty) {
  if (isDirty() && !confirm('Unsaved workflow changes will be lost. Stop anyway?')) return;
  const button = $('stopButton');
  button.disabled = true;
  $('projectError').hidden = true;
  try {
    await api('/api/project/stop', { method: 'POST' });
    reloadToAttachProject();
  } catch (error) {
    showError(error.message);
    button.disabled = false;
  }
}

export async function initProjectBar({ isDirty }) {
  const status = await api('/api/project');
  renderProjects(status);
  renderState(status);
  $('runButton').addEventListener('click', run);
  $('stopButton').addEventListener('click', () => stop(isDirty));
  return status;
}
