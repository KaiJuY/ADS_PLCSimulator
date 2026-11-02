const SVG_NS = "http://www.w3.org/2000/svg";
const TWO_PI = Math.PI * 2;
const byId = (id) => document.getElementById(id);
const statusEl = document.querySelector("[data-corner='hint']");
const errorsEl = byId("errors");
const labelsLayer = byId("Labels");
const targetByKey = new Map(TARGETS.map((entry) => [entry.key.toLowerCase(), entry]));

const ease = (p) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2);
const tween = (value) => ({ from: value, to: value, start: 0, dur: 0, rate: 0, min: 0, max: 0 });
const settled = (t, now) => t.rate === 0 && (t.dur <= 0 || now - t.start >= t.dur);
function valueAt(t, now) {
  if (t.rate !== 0) return Math.min(t.max, Math.max(t.min, t.from + (t.rate * (now - t.start)) / 1000));
  if (settled(t, now)) return t.to;
  return t.from + (t.to - t.from) * ease((now - t.start) / t.dur);
}

const GRIPPER_GOAL = { Open: 0, Close: 1 };
const VENT_GOAL = { Open: 1, Close: 0 };
const DAMPER_GOAL = { AEX: 0, SEX: 1 };

function freshState() {
  const spin = () => ({ dir: 0, angle: 0, since: 0, rpm: 0, home: null });
  return {
    cups: CUP_OUTER_R.map((_, index) => ({ Master: tween(CUP_RIM[CUP_INITIAL][index]), Slave: tween(CUP_RIM[CUP_INITIAL][index]) })),
    lift: CORNERS.map(() => tween(ARM_Z[ARM_LIFT_INITIAL])),
    rotate: CORNERS.map(() => tween(ARM_ROTATE_DEG[ARM_ROTATE_INITIAL])),
    spin: { Holder: spin(), OutsideRing: spin() },
    gripper: tween(GRIPPER_GOAL[GRIPPER_INITIAL]),
    ringLift: tween(RING_LIFT[RING_LIFT_INITIAL]),
    shutter: tween(SHUTTER_Z[SHUTTER_INITIAL]),
    vents: EXHAUST_ANGLE_DEG.map(() => tween(VENT_GOAL[VENT_INITIAL])),
    damper: tween(DAMPER_GOAL[DAMPER_INITIAL]),
    fans: EXHAUST_ANGLE_DEG.map(() => ({ on: false, angle: 0, since: 0 })),
    active: {},
    labels: {},
    lastAction: {},
    stepGoal: {},
  };
}
let state = freshState();
let projectName = null;

const spinAngle = (s, now) => s.angle + (s.dir * SPIN_DEG_PER_S * (Math.PI / 180) * (now - s.since)) / 1000;
const shownAngle = (s, now) => (s.home ? valueAt(s.home, now) : spinAngle(s, now));
const fanAngle = (fan, now) => fan.angle + (fan.on ? (FAN.degPerS * (Math.PI / 180) * (now - fan.since)) / 1000 : 0);

function moveTo(t, goal, durationMs, now) {
  const current = valueAt(t, now);
  const duration = durationMs === null || durationMs === undefined ? TWEEN_DEFAULT_MS : Number(durationMs);
  if (goal === null) {
    Object.assign(t, { from: current, to: current, start: now, dur: 0, rate: 0 });
    return;
  }
  if (!(duration > 0)) {
    Object.assign(t, { from: goal, to: goal, start: now, dur: 0, rate: 0 });
    return;
  }
  Object.assign(t, { from: current, to: goal, start: now, dur: duration, rate: 0 });
}

function jogFrom(t, rate, min, max, now) {
  const from = Math.min(max, Math.max(min, valueAt(t, now)));
  Object.assign(t, { from, to: from, start: now, dur: 0, rate, min, max });
}

const goalOf = (table, action, index) => (action === "Hold" ? null : (index === undefined ? table[action] : table[action][index]));

const ROTATE_VALUES = Object.values(ARM_ROTATE_DEG);
const AXES = {
  rotate: (entry) => ({ t: state.rotate[entry.index], min: Math.min(...ROTATE_VALUES), max: Math.max(...ROTATE_VALUES), rate: JOG.rotateDegPerS, step: JOG.rotateStepDeg, goals: ARM_ROTATE_DEG, goalIndex: undefined }),
  cup: (entry) => ({ t: state.cups[entry.index][entry.side], min: CUP_RIM.Down[entry.index], max: CUP_RIM.Top[entry.index], rate: JOG.cupMmPerS, step: JOG.cupStepMm, goals: CUP_RIM, goalIndex: entry.index }),
};
const clampTo = (axis, value) => Math.min(axis.max, Math.max(axis.min, value));
const isJog = (action) => JOG_ACTIONS.includes(action);

const storageKey = (key) => STORAGE_PREFIX + projectName + ":" + key;
function storeRead(key) {
  if (!projectName) return null;
  try {
    const raw = window.localStorage.getItem(storageKey(key));
    const number = raw === null ? NaN : Number(raw);
    return Number.isFinite(number) ? number : null;
  } catch {
    return null;
  }
}
function storeWrite(key, value) {
  if (!projectName) return;
  try {
    window.localStorage.setItem(storageKey(key), String(value));
  } catch {
    // storage unavailable: the next reload shows the initial position
  }
}
function storeRemove(key) {
  if (!projectName) return;
  try {
    window.localStorage.removeItem(storageKey(key));
  } catch {
    // storage unavailable
  }
}

function startStep(entry, axis, command, now) {
  const step = Math.sign(Number(command.value));
  if (!step) return;
  const current = valueAt(axis.t, now);
  const pending = state.stepGoal[entry.key];
  const reuse = pending !== undefined && [step, 0].includes(Math.sign(pending - current));
  const goal = reuse ? pending : clampTo(axis, current + step * axis.step);
  state.stepGoal[entry.key] = goal;
  moveTo(axis.t, goal, command.durationMs, now);
}

function finishStep(entry, axis, now) {
  const goal = state.stepGoal[entry.key];
  if (goal === undefined) return;
  delete state.stepGoal[entry.key];
  moveTo(axis.t, goal, 0, now);
}

function jogAction(entry, axis, action, now, command) {
  if (action === "JogPositive") return jogFrom(axis.t, axis.rate, axis.min, axis.max, now);
  if (action === "JogNegative") return jogFrom(axis.t, -axis.rate, axis.min, axis.max, now);
  if (action === "IncrementalJog") return startStep(entry, axis, command, now);
  return finishStep(entry, axis, now);
}

function restoreJog(entry, axis, now) {
  const stored = storeRead(entry.key);
  if (stored === null) return;
  moveTo(axis.t, clampTo(axis, stored), 0, now);
}

function moveAxis(entry, action, now, command, replay) {
  const axis = AXES[entry.type](entry);
  if (!isJog(action)) return moveTo(axis.t, goalOf(axis.goals, action, axis.goalIndex), command.durationMs, now);
  if (replay) return restoreJog(entry, axis, now);
  return jogAction(entry, axis, action, now, command);
}

function homeTo(s, now, command) {
  const from = shownAngle(s, now);
  s.angle = from;
  s.since = now;
  s.dir = 0;
  s.rpm = 0;
  s.home = tween(from);
  moveTo(s.home, TWO_PI * Math.round(from / TWO_PI), command.durationMs, now);
}

const SPIN_DIR = { CW: 1, CCW: -1, Stop: 0, Home: 0 };
const HANDLERS = {
  spin(entry, action, now, command) {
    const s = state.spin[entry.key];
    if (action === "Home") return homeTo(s, now, command);
    s.angle = shownAngle(s, now) % TWO_PI;
    s.since = now;
    s.home = null;
    if (action === "Hold") return;
    s.dir = SPIN_DIR[action];
    const rpm = Number(command.value);
    s.rpm = action !== "Stop" && Number.isFinite(rpm) && command.value !== null ? rpm : 0;
  },
  cup: moveAxis,
  rotate: moveAxis,
  lift(entry, action, now, command) {
    moveTo(state.lift[entry.index], goalOf(ARM_Z, action), command.durationMs, now);
  },
  gripper(entry, action, now, command) {
    moveTo(state.gripper, goalOf(GRIPPER_GOAL, action), command.durationMs, now);
  },
  ringLift(entry, action, now, command) {
    moveTo(state.ringLift, goalOf(RING_LIFT, action), command.durationMs, now);
  },
  shutter(entry, action, now, command) {
    moveTo(state.shutter, goalOf(SHUTTER_Z, action), command.durationMs, now);
  },
  vent(entry, action, now, command) {
    moveTo(state.vents[entry.index], goalOf(VENT_GOAL, action), command.durationMs, now);
  },
  damper(entry, action, now, command) {
    moveTo(state.damper, goalOf(DAMPER_GOAL, action), command.durationMs, now);
  },
  drain() {},
  exhaust(entry, action, now) {
    HANDLERS.switch(entry, action);
    const fan = state.fans[entry.index];
    const on = action === "On";
    if (fan.on === on) return;
    fan.angle = fanAngle(fan, now);
    fan.since = now;
    fan.on = on;
  },
  switch(entry, action) {
    state.active[entry.key] = action === "On";
  },
};
const handlerOf = (type) => HANDLERS[type] ?? HANDLERS.switch;

function formatLabel(value, unit) {
  if (value === null || value === undefined) return null;
  const number = Number(value);
  return (Number.isFinite(number) ? number.toFixed(1) : String(value)) + " " + unit;
}

// A label function returns undefined to keep the shown label, null to clear it, or the new text.
const positionLabel = (entry, action, value) => (value === null || value === undefined ? undefined : formatLabel(value, ACTION_UNITS[entry.type]?.[action] ?? UNITS[entry.type]));
const spinLabel = (entry, action, value) => {
  if (action === "CW" || action === "CCW") return formatLabel(value, "RPM");
  return null;
};
const actionLabel = (entry, action) => action;
const LABELS = {
  rotate: positionLabel,
  cup: positionLabel,
  lift: positionLabel,
  spin: spinLabel,
  gripper: actionLabel,
  ringLift: actionLabel,
  shutter: actionLabel,
  vent: actionLabel,
  damper: actionLabel,
  drain: (entry, action) => "Drain " + action.slice(-1),
};
const labelOf = (entry, action, value) => (LABELS[entry.type] ?? ((e, a, v) => formatLabel(v, e.unit)))(entry, action, value);

const errorLines = [];
function reportUnknown(command) {
  errorLines.push("未知指令：" + command.target + " " + command.action);
  if (errorLines.length > ERROR_LINES) errorLines.splice(0, errorLines.length - ERROR_LINES);
  errorsEl.textContent = errorLines.join("\n");
}

function afterAction(entry, action, now) {
  const axis = AXES[entry.type];
  if (action === "Hold") {
    if (axis && isJog(state.lastAction[entry.key])) storeWrite(entry.key, valueAt(axis(entry).t, now));
    return;
  }
  state.lastAction[entry.key] = action;
  if (axis && !isJog(action)) storeRemove(entry.key);
}

function apply(command, now, replay = false) {
  const entry = command ? targetByKey.get(String(command.target).toLowerCase()) : undefined;
  const action = entry?.actions.find((name) => name.toLowerCase() === String(command.action).toLowerCase());
  if (!action) {
    reportUnknown(command ?? { target: "", action: "" });
    return;
  }
  if (!["IncrementalJog", "StepDone", "Hold"].includes(action)) delete state.stepGoal[entry.key];
  handlerOf(entry.type)(entry, action, now, command, replay);
  afterAction(entry, action, now);
  if (action === "Hold") return;
  const label = labelOf(entry, action, command.value);
  if (label !== undefined) state.labels[entry.key] = label;
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
  state.cups.forEach((cup, index) => {
    const [west, east] = [valueAt(cup.Slave, now), valueAt(cup.Master, now)];
    const key = `${west},${east}`;
    const id = "Cup" + (index + 1);
    if (drawn.get(id) === key) return;
    drawn.set(id, key);
    const parts = cupParts(index, west, east);
    byId(id + "-back").innerHTML = parts.back;
    byId(id + "-front").innerHTML = parts.front;
  });
}

function drawFixed() {
  FIXED_ANGLE_DEG.forEach((_, index) => show("FixedNozzle" + (index + 1) + "-jet", state.active["FixedNozzle" + (index + 1)]));
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

function drawRingLift(now) {
  const rise = at([0, 0, valueAt(state.ringLift, now)])[1] - at([0, 0, 0])[1];
  const transform = `translate(0 ${rise.toFixed(2)})`;
  for (const id of ["OutsideRing-back", "OutsideRing-front"]) {
    if (drawn.get(id) === transform) continue;
    drawn.set(id, transform);
    byId(id).setAttribute("transform", transform);
  }
}

function drawExhaust(now) {
  state.fans.forEach((fan, index) => {
    const angle = fanAngle(fan, now);
    update("Fan" + (index + 1), String(angle % TWO_PI), () => fanSvg(index, angle));
    const open = valueAt(state.vents[index], now);
    update("SideVent" + (index + 1), String(open), () => ventSvg(index, open));
  });
  update("Damper", String(valueAt(state.damper, now)), () => damperSvg(valueAt(state.damper, now)));
}

function draw(now) {
  const holder = shownAngle(state.spin.Holder, now);
  const clamp = valueAt(state.gripper, now);
  const ring = shownAngle(state.spin.OutsideRing, now);
  const shutter = valueAt(state.shutter, now);
  update("Holder", `${holder % TWO_PI},${clamp}`, () => holderSvg(holder, clamp));
  update("OutsideRing-ticks", String(ring % TWO_PI), () => ringTicksSvg(ring));
  update("Shutter", String(shutter), () => shutterSvg(shutter));
  drawRingLift(now);
  drawCups(now);
  drawFixed();
  drawArms(now);
  drawExhaust(now);
  drawLabels();
}

function busy(now) {
  const tweens = [...state.lift, ...state.rotate, ...state.cups.flatMap((cup) => [cup.Master, cup.Slave]), state.gripper, state.ringLift, state.shutter, ...state.vents, state.damper];
  const homes = [state.spin.Holder.home, state.spin.OutsideRing.home].filter(Boolean);
  const moving = [...tweens, ...homes].some((t) => !settled(t, now));
  return moving || state.spin.Holder.dir !== 0 || state.spin.OutsideRing.dir !== 0 || state.fans.some((fan) => fan.on);
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
    projectName = data.running && data.name ? String(data.name) : null;
    drawn.clear();
    setStatus(data.running ? String(data.name ?? "") : "Polar2 未執行");
  }
  if (data.type === "simState") (Array.isArray(data.commands) ? data.commands : []).forEach((command) => apply({ ...command, durationMs: 0 }, now, true));
  if (data.type === "sim") apply(data.command, now, false);
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
