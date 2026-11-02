const SVG_NS = "http://www.w3.org/2000/svg";
const byId = (id) => document.getElementById(id);
const statusEl = document.querySelector("[data-corner='hint']");
const errorsEl = byId("errors");
const labelsLayer = byId("Labels");
const targetByKey = new Map(TARGETS.map((entry) => [entry.key.toLowerCase(), entry]));

const ease = (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2);
const tween = (value) => ({ from: value, to: value, start: 0, dur: 0 });
const settled = (t, now) => t.dur <= 0 || now - t.start >= t.dur;
function valueAt(t, now) {
  if (settled(t, now)) return t.to;
  return t.from + (t.to - t.from) * ease((now - t.start) / t.dur);
}

function freshState() {
  const spin = () => ({ dir: 0, angle: 0, since: 0 });
  return {
    cups: CUP_OUTER_R.map((_, index) => ({ Master: tween(CUP_RIM[CUP_INITIAL][index]), Slave: tween(CUP_RIM[CUP_INITIAL][index]) })),
    lift: CORNERS.map(() => tween(ARM_Z[ARM_LIFT_INITIAL])),
    rotate: CORNERS.map(() => tween(ARM_ROTATE_DEG[ARM_ROTATE_INITIAL])),
    spin: { Holder: spin(), OutsideRing: spin() },
    active: {},
    labels: {},
  };
}
let state = freshState();

const spinAngle = (s, now) => s.angle + (s.dir * SPIN_DEG_PER_S * (Math.PI / 180) * (now - s.since)) / 1000;

function moveTo(t, goal, durationMs, now) {
  const current = valueAt(t, now);
  const duration = durationMs === null || durationMs === undefined ? TWEEN_DEFAULT_MS : Number(durationMs);
  if (goal === null) {
    Object.assign(t, { from: current, to: current, start: now, dur: 0 });
    return;
  }
  if (!(duration > 0)) {
    Object.assign(t, { from: goal, to: goal, start: now, dur: 0 });
    return;
  }
  Object.assign(t, { from: current, to: goal, start: now, dur: duration });
}

const goalOf = (table, action, index) => (action === "Hold" ? null : (index === undefined ? table[action] : table[action][index]));

const HANDLERS = {
  spin(entry, action, now) {
    const s = state.spin[entry.key];
    s.angle = spinAngle(s, now) % (Math.PI * 2);
    s.since = now;
    s.dir = action === "CW" ? 1 : action === "CCW" ? -1 : 0;
  },
  cup(entry, action, now, command) {
    moveTo(state.cups[entry.index][entry.side], goalOf(CUP_RIM, action, entry.index), command.durationMs, now);
  },
  lift(entry, action, now, command) {
    moveTo(state.lift[entry.index], goalOf(ARM_Z, action), command.durationMs, now);
  },
  rotate(entry, action, now, command) {
    moveTo(state.rotate[entry.index], goalOf(ARM_ROTATE_DEG, action), command.durationMs, now);
  },
  switch(entry, action) {
    state.active[entry.key] = action === "On";
  },
};
// A position keeps its last label when a command has no value (the leave Hold); a spin or switch shows nothing once it stops.
const POSITION_TYPES = new Set(["cup", "lift", "rotate"]);
const handlerOf = (type) => HANDLERS[type] ?? HANDLERS.switch;

function formatLabel(value, unit) {
  if (value === null || value === undefined) return null;
  const number = Number(value);
  return (Number.isFinite(number) ? number.toFixed(1) : String(value)) + " " + unit;
}

const errorLines = [];
function reportUnknown(command) {
  errorLines.push("未知指令：" + command.target + " " + command.action);
  if (errorLines.length > ERROR_LINES) errorLines.splice(0, errorLines.length - ERROR_LINES);
  errorsEl.textContent = errorLines.join("\n");
}

function apply(command, now) {
  const entry = command ? targetByKey.get(String(command.target).toLowerCase()) : undefined;
  const action = entry?.actions.find((name) => name.toLowerCase() === String(command.action).toLowerCase());
  if (!action) {
    reportUnknown(command ?? { target: "", action: "" });
    return;
  }
  handlerOf(entry.type)(entry, action, now, command);
  const label = formatLabel(command.value, entry.unit);
  if (label === null && POSITION_TYPES.has(entry.type)) return;
  state.labels[entry.key] = label;
}

const labelBlockNodes = labelBlocks().map((spec) => {
  const lines = spec.lines.map((line) => {
    const text = document.createElementNS(SVG_NS, "text");
    text.setAttribute("class", "part-label");
    text.setAttribute("text-anchor", spec.align);
    const key = document.createElementNS(SVG_NS, "tspan");
    key.setAttribute("class", "lbl-key");
    key.textContent = line.prefix + " ";
    const value = document.createElementNS(SVG_NS, "tspan");
    text.append(key, value);
    text.style.display = "none";
    labelsLayer.appendChild(text);
    return { line, text, value };
  });
  return { spec, lines };
});

function drawLabels() {
  for (const { spec, lines } of labelBlockNodes) {
    let row = 0;
    for (const { line, text, value } of lines) {
      const label = line.keys.map((key) => state.labels[key]).find((entry) => entry);
      text.style.display = label ? "" : "none";
      if (!label) continue;
      value.textContent = label;
      text.setAttribute("x", spec.anchor[0].toFixed(1));
      text.setAttribute("y", (spec.anchor[1] + row * LABEL_LINE_PX).toFixed(1));
      row++;
    }
  }
}

const drawn = new Map();
function update(id, key, make) {
  if (drawn.get(id) === key) return;
  drawn.set(id, key);
  byId(id).innerHTML = make();
}
const show = (id, on) => { byId(id).style.display = on ? "" : "none"; };

function drawCups(now) {
  const rims = state.cups.map((cup) => [valueAt(cup.Slave, now), valueAt(cup.Master, now)]);
  rims.forEach(([west, east], index) => {
    const key = `${west},${east}`;
    const id = "Cup" + (index + 1);
    if (drawn.get(id) === key) return;
    drawn.set(id, key);
    const parts = cupParts(index, west, east);
    byId(id + "-back").innerHTML = parts.back;
    byId(id + "-front").innerHTML = parts.front;
  });
  return rims[3];
}

function drawFixed(rim) {
  FIXED_ANGLE_DEG.forEach((_, index) => {
    const id = "FixedNozzle" + (index + 1);
    update(id + "-base", `${rim[0]},${rim[1]}`, () => fixedNozzleParts(index, rim[0], rim[1]).base);
    show(id + "-jet", state.active[id]);
    if (state.active[id]) update(id + "-jet", `${rim[0]},${rim[1]}`, () => fixedNozzleParts(index, rim[0], rim[1]).jet);
  });
}

function drawArms(now) {
  CORNERS.forEach((_, index) => {
    const rotate = valueAt(state.rotate[index], now);
    const lift = valueAt(state.lift[index], now);
    const key = `${rotate},${lift}`;
    const id = "Arm" + (index + 1);
    update(id, key, () => armBodySvg(index, rotate, lift));
    for (let nozzle = 0; nozzle < NOZZLE_COUNT[index]; nozzle++) {
      const jet = id + "-Nozzle" + (nozzle + 1) + "-jet";
      const on = state.active[id + ".Nozzle" + (nozzle + 1)];
      show(jet, on);
      if (on) update(jet, key, () => armJetSvg(index, nozzle, rotate, lift));
    }
    for (let port = 0; port < PORT_COUNT[index]; port++) show(id + "-Port" + (port + 1) + "-jet", state.active[id + ".Port" + (port + 1)]);
  });
}

function draw(now) {
  const holder = spinAngle(state.spin.Holder, now);
  const ring = spinAngle(state.spin.OutsideRing, now);
  update("Holder", String(holder % (Math.PI * 2)), () => holderSvg(holder));
  update("OutsideRing-ticks", String(ring % (Math.PI * 2)), () => ringTicksSvg(ring));
  drawFixed(drawCups(now));
  drawArms(now);
  EXHAUST_ANGLE_DEG.forEach((_, index) => show("ExhaustFan" + (index + 1) + "-flow", state.active["ExhaustFan" + (index + 1)]));
  drawLabels();
}

function busy(now) {
  const moving = [...state.lift, ...state.rotate, ...state.cups.flatMap((cup) => [cup.Master, cup.Slave])].some((t) => !settled(t, now));
  return moving || state.spin.Holder.dir !== 0 || state.spin.OutsideRing.dir !== 0;
}

let frame = 0;
const visible = () => document.visibilityState === "visible";
function refresh() {
  if (!visible()) return;
  const now = performance.now();
  draw(now);
  if (busy(now) && !frame) frame = requestAnimationFrame(tick);
}
function tick() {
  frame = 0;
  refresh();
}
document.addEventListener("visibilitychange", refresh);

const setStatus = (text) => { statusEl.textContent = text; };

function onMessage(data) {
  if (typeof data !== "object" || data === null) return;
  const now = performance.now();
  if (data.type === "project") {
    state = freshState();
    drawn.clear();
    setStatus(data.running ? String(data.name ?? "") : "Polar2 未執行");
  }
  if (data.type === "simState") (Array.isArray(data.commands) ? data.commands : []).forEach((command) => apply({ ...command, durationMs: 0 }, now));
  if (data.type === "sim") apply(data.command, now);
  refresh();
}

function connect(delay = 500) {
  if (location.protocol === "file:") {
    setStatus("靜態預覽（未連線）");
    return;
  }
  const socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live");
  socket.addEventListener("open", () => {
    delay = 500;
    setStatus("已連線");
  });
  socket.addEventListener("message", (message) => {
    let data;
    try {
      data = JSON.parse(message.data);
    } catch {
      return;
    }
    onMessage(data);
  });
  socket.addEventListener("close", () => {
    setStatus("離線，重新連線中");
    setTimeout(() => connect(Math.min(delay * 2, 8000)), delay);
  });
}

connect();
