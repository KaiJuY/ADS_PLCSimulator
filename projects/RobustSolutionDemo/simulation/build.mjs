// Builds index.html: an isometric Chamber1 that the simulator's workflows drive through "sim.invoke" nodes.
// Run: node build.mjs [--light]
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import * as k from "./kit/iso-kit.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const theme = process.argv.includes("--light") ? "light" : "dark";
// The kit's page templates contain "</body>" and "</html>". Visual Studio Browser Link and dotnet-watch inject their
// scripts before the first "</body>" in a response, which would land inside this inline script and end it early.
const kitScript = () => readFileSync(join(HERE, "kit", "iso-kit.mjs"), "utf8")
  .replace(/^export /gm, "")
  .replace(/<\/(body|html)/gi, "<\\/$1");

const WIDTH = 640;
const HEIGHT = 440;

const PLATE = { x: -150, y: -110, w: 300, d: 230, r: 8 };
const PLATE_H = 12;
const FOOT_H = 5;

const CHAMBER = { x: -70, y: -70, w: 140, d: 140, r: 10 };
const LEG_TOP = 24;
const FLOOR_TOP = 32;
const WALL = 8;
const WALL_TOP = 66;

const PEDESTAL_TOP = 46;
const CHUCK_R = 34;
const CHUCK_TOP = 54;
const WAFER_R = 30;
const PIN_R = 1.6;
const PIN_ORBIT = 22;
const PIN_ANGLES = [90, 210, 330].map((deg) => (deg * Math.PI) / 180);
const PIN_DOWN = CHUCK_TOP - 1;
const PIN_UP = CHUCK_TOP + 13;

const SLOT = { y0: -25, y1: 25, z0: 44, z1: 56 };
const GATE = { x: 71.5, y: -28, w: 4, d: 56, r: 1 };
const GATE_Z = 42;
const GATE_H = 18;
const GATE_TRAVEL = 17;
const BONNET = { x: 70, y: -32, w: 11, d: 64, r: 2 };
const BONNET_Z = 30;
const BONNET_TOP = 44;

const BOX = { x: -138, y: 70, w: 58, d: 34, r: 3 };
const BOX_H = 16;
const GAUGE = [-118, 87];
const GAUGE_R = 10;
const GAUGE_TOP = BOX_H + 4;
const LAMP = [-92, 80];
const LAMP_TOP = BOX_H + 4;
const GAUGE_SWEEP = 1.5 * Math.PI;
const LOG_P = [3, -3];

const P = k.fitProjection(
  [...k.boxCorners(PLATE, -PLATE_H - FOOT_H, 0), ...k.boxCorners(CHAMBER, 0, WALL_TOP), [GATE.x, 0, GATE_Z + GATE_H], [0, 0, PIN_UP + 2]],
  WIDTH,
  HEIGHT,
  { pad: 28, azimuth: 50, elevation: 38 },
);

const at = (point) => k.iso(point, P);
const S = (paths, style) => k.solidSvg(paths, style);
const L = (d, style) => k.lineSvg(d, style);
const D = (points, style) => k.dotsSvg(points, style);
const depth = ([x, y, z = 0]) => k.depthOf([x, y, z], P);
const byDepth = (points) => [...points].sort((a, b) => depth(a) - depth(b));

function plate() {
  const out = [`<path class="iso-halo" d="${k.haloOf(PLATE, -PLATE_H - FOOT_H, PLATE_H, P)}"/>`];
  for (const [x, y] of byDepth(k.corners(PLATE, 22))) {
    out.push(S(k.cylinder(x, y, 7, -PLATE_H - FOOT_H, 1.6, P, 28), { tone: "lo" }));
    out.push(S(k.cylinder(x, y, 5.4, -PLATE_H - FOOT_H + 1.6, FOOT_H - 1.6, P, 28), { tone: "mid" }));
  }
  out.push(S(k.slabOf(PLATE, -PLATE_H, PLATE_H, P, 8, 1.6), { tone: "mid" }));
  out.push(L(k.planOutline(k.insetPlan(PLATE, 5), 0, P), { tone: "lo" }));
  const screws = k.corners(PLATE, 10);
  out.push(L(screws.map(([x, y]) => k.ring(x, y, 2.2, 0, P, 16)).join(""), { tone: "lo" }));
  out.push(D(screws.map(([x, y]) => at([x, y, 0])), { size: 0.5 }));

  const strip = { x: -62, y: PLATE.y + PLATE.d - 16, w: 196, d: 7, r: 1 };
  out.push(S(k.slabOf(strip, 0, 0.6, P, 3), { tone: "lo", crease: "none" }));
  const ticks = k.topTicks(strip.x + 4, strip.x + strip.w - 4, 4, 5, strip.y + 0.8, 0.6, [2, 3.6], P);
  out.push(L(ticks.minor, { tone: "lo" }), L(ticks.major, { tone: "mid" }));
  out.push(D([at([strip.x + 2, strip.y + 3.5, 0.6]), at([strip.x + strip.w - 2, strip.y + 3.5, 0.6])], { size: 0.45 }));

  const label = { x: 92, y: -100, w: 44, d: 22, r: 1.5 };
  out.push(S(k.slabOf(label, 0, 0.8, P, 4), { tone: "lo", crease: "none" }));
  out.push(L(k.planOutline(k.insetPlan(label, 1.6), 0.8, P, 4), { tone: "faint" }));
  [[6, 30], [6, 22], [6, 26]].forEach(([from, length], row) => {
    out.push(L(k.lineOnTop([label.x + from, label.y + 6 + row * 5], [label.x + from + length, label.y + 6 + row * 5], 0.8, P), { tone: "lo", free: true }));
  });
  out.push(D([at([label.x + 40, label.y + 4, 0.8]), at([label.x + 40, label.y + label.d - 4, 0.8])], { size: 0.45 }));
  return out.join("");
}

const LEGS = k.corners(CHAMBER, 12);
const legSvg = ([x, y]) => {
  const plan = { x: x - 5, y: y - 5, w: 10, d: 10, r: 1.5 };
  return [
    S(k.cylinder(x, y, 8, 0, 1.6, P, 24), { tone: "lo" }),
    S(k.slabOf(plan, 1.6, LEG_TOP - 1.6, P, 3, 0.4), { tone: "mid" }),
    D([at([x, y, 0.8 + 0.8])], { size: 0.4 }),
  ].join("");
};

const wallPlans = {
  back: { x: CHAMBER.x, y: CHAMBER.y, w: CHAMBER.w, d: WALL, r: 2 },
  left: { x: CHAMBER.x, y: CHAMBER.y + WALL, w: WALL, d: CHAMBER.d - 2 * WALL, r: 0.5 },
  front: { x: CHAMBER.x, y: CHAMBER.y + CHAMBER.d - WALL, w: CHAMBER.w, d: WALL, r: 2 },
  right: { x: CHAMBER.x + CHAMBER.w - WALL, y: CHAMBER.y + WALL, w: WALL, d: CHAMBER.d - 2 * WALL, r: 0.5 },
};

function wall(plan) {
  const height = WALL_TOP - FLOOR_TOP;
  const seam = k.sideSeam(plan, FLOOR_TOP + 6, P, 3);
  const out = [S(k.slabOf(plan, FLOOR_TOP, height, P, 3, 0.5), { tone: "mid", inner: L(seam, { tone: "faint" }) })];
  const along = plan.w > plan.d;
  const holes = along
    ? [plan.x + 14, plan.x + plan.w / 2, plan.x + plan.w - 14].map((x) => [x, plan.y + plan.d / 2])
    : [plan.y + 12, plan.y + plan.d / 2, plan.y + plan.d - 12].map((y) => [plan.x + plan.w / 2, y]);
  out.push(L(holes.map(([x, y]) => k.ring(x, y, 1.6, WALL_TOP, P, 12)).join(""), { tone: "lo" }));
  out.push(D(holes.map(([x, y]) => at([x, y, WALL_TOP])), { size: 0.35, tone: "lo" }));
  return out.join("");
}

function floor() {
  const out = [];
  out.push(S(k.slabOf(CHAMBER, LEG_TOP, FLOOR_TOP - LEG_TOP, P, 8, 0.8), { tone: "mid" }));
  const inner = k.insetPlan(CHAMBER, WALL);
  out.push(L(k.planOutline(k.insetPlan(inner, 4), FLOOR_TOP, P, 4), { tone: "faint" }));
  out.push(L(k.ring(0, 0, CHUCK_R + 8, FLOOR_TOP, P, 64), { tone: "lo" }));
  return out.join("");
}

function pedestal() {
  const out = [];
  out.push(S(k.cylinder(0, 0, 20, FLOOR_TOP, 2, P, 48), { tone: "lo" }));
  out.push(S(k.cylinder(0, 0, 14, FLOOR_TOP + 2, PEDESTAL_TOP - FLOOR_TOP - 2, P, 40), { tone: "mid", crease: "none" }));
  out.push(L(k.sideArc(0, 0, 14, FLOOR_TOP + 7, P, 28), { tone: "faint" }));
  out.push(S(k.cylinder(0, 0, CHUCK_R, PEDESTAL_TOP, CHUCK_TOP - PEDESTAL_TOP, P, 72, 0.6), { tone: "hi", crease: "lo" }));
  out.push(L(k.sideArc(0, 0, CHUCK_R, PEDESTAL_TOP + 3, P, 56), { tone: "lo" }));
  out.push(L(k.ring(0, 0, WAFER_R + 0.6, CHUCK_TOP, P, 72), { tone: "faint" }));
  out.push(L(PIN_ANGLES.map((a) => k.ring(Math.cos(a) * PIN_ORBIT, Math.sin(a) * PIN_ORBIT, PIN_R + 0.6, CHUCK_TOP, P, 12)).join(""), { tone: "lo" }));
  return out.join("");
}

// Parts rebuilt every frame: lift pins, wafer and the chuck's turning tick ring.
function pinsAt(tip) {
  if (tip <= CHUCK_TOP + 0.2) return "";
  const pins = PIN_ANGLES.map((a) => [Math.cos(a) * PIN_ORBIT, Math.sin(a) * PIN_ORBIT]);
  return byDepth(pins).map(([x, y]) => S(k.cylinder(x, y, PIN_R, CHUCK_TOP, tip - CHUCK_TOP, P, 14), { tone: "mid", crease: "none" })
    + (tip - CHUCK_TOP > 1.2 ? S(k.cylinder(x, y, PIN_R + 0.6, tip - 0.8, 0.8, P, 14), { tone: "hi", crease: "lo" }) : "")).join("");
}

function waferAt(z, angle) {
  const notchFrom = [Math.cos(angle) * (WAFER_R - 6), Math.sin(angle) * (WAFER_R - 6)];
  const notchTo = [Math.cos(angle) * (WAFER_R - 0.6), Math.sin(angle) * (WAFER_R - 0.6)];
  return [
    S(k.cylinder(0, 0, WAFER_R, z, 1, P, 72), { tone: "hi", lit: true }),
    L(k.ring(0, 0, WAFER_R - 2.5, z + 1, P, 72), { tone: "faint" }),
    L(k.lineOnTop(notchFrom, notchTo, z + 1, P), { tone: "lit", free: "start" }),
  ].join("");
}

const WAFER_SHADOW = `<path class="iso-halo" d="${k.haloOf({ x: -WAFER_R, y: -WAFER_R, w: WAFER_R * 2, d: WAFER_R * 2, r: WAFER_R }, CHUCK_TOP, 0.2, P)}" style="filter:blur(3px)"/>`;

function chuckTicksAt(angle) {
  const ticks = k.radialTicks(0, 0, CHUCK_R - 1, 36, 6, CHUCK_TOP, [1.2, 2.4], P, angle);
  return L(ticks.minor, { tone: "lo" }) + L(ticks.major, { tone: "mid" });
}

function slitValveFrame() {
  const out = [];
  const slot = [
    [70.05, SLOT.y0, SLOT.z0], [70.05, SLOT.y1, SLOT.z0], [70.05, SLOT.y1, SLOT.z1], [70.05, SLOT.y0, SLOT.z1],
  ].map(at);
  out.push(`<path d="${k.pathOf(slot, true)}" style="fill:var(--anatomy-shade-0);stroke:var(--anatomy-lo);stroke-width:var(--anatomy-weight)"/>`);
  return out.join("");
}

function gate() {
  const out = [S(k.slabOf(GATE, GATE_Z, GATE_H, P, 2, 0.4), { tone: "hi", crease: "lo" })];
  out.push(L(k.sideSeam(GATE, GATE_Z + GATE_H - 4, P, 2), { tone: "lo" }));
  out.push(D([[GATE.x + GATE.w, -18], [GATE.x + GATE.w, 18]].map(([x, y]) => at([x, y, GATE_Z + GATE_H - 8])), { size: 0.45 }));
  return out.join("");
}

function bonnet() {
  const out = [S(k.slabOf(BONNET, BONNET_Z, BONNET_TOP - BONNET_Z, P, 3, 0.5), { tone: "mid" })];
  out.push(L(k.planOutline({ x: GATE.x - 0.4, y: GATE.y - 0.4, w: GATE.w + 0.8, d: GATE.d + 0.8, r: 1 }, BONNET_TOP, P, 2), { tone: "lo" }));
  out.push(D([[BONNET.x + 3, BONNET.y + 3], [BONNET.x + 3, BONNET.y + BONNET.d - 3], [BONNET.x + BONNET.w - 2.5, BONNET.y + 3], [BONNET.x + BONNET.w - 2.5, BONNET.y + BONNET.d - 3]].map(([x, y]) => at([x, y, BONNET_TOP])), { size: 0.45 }));
  const cx = BONNET.x + BONNET.w / 2;
  out.push(S(k.cylinder(cx, 0, 4, BONNET_Z - 2, 2, P, 24), { tone: "lo" }));
  out.push(S(k.cylinder(cx, 0, 6.5, 12, BONNET_Z - 14, P, 32), { tone: "mid", crease: "none" }));
  out.push(L(k.sideArc(cx, 0, 6.5, 22, P, 20), { tone: "faint" }));
  out.push(S(k.cylinder(cx, 0, 7.2, 10, 2, P, 32), { tone: "mid" }));
  out.push(S(k.slabOf({ x: cx + 5.5, y: 6, w: 3, d: 3, r: 1 }, 14, 3, P, 2), { tone: "lo", crease: "none" }));
  out.push(S(k.slabOf({ x: cx + 5.5, y: -9, w: 3, d: 3, r: 1 }, 24, 3, P, 2), { tone: "lo", crease: "none" }));
  return out.join("");
}

function controlBox() {
  const out = [`<path class="iso-halo" d="${k.haloOf(BOX, 0, BOX_H, P)}" style="filter:blur(5px)"/>`];
  out.push(S(k.slabOf(BOX, 0, BOX_H, P, 4, 0.8), { tone: "mid" }));
  out.push(L(k.planOutline(k.insetPlan(BOX, 2.5), BOX_H, P, 3), { tone: "faint" }));
  out.push(D(k.corners(BOX, 4.5).map(([x, y]) => at([x, y, BOX_H])), { size: 0.45 }));
  const [gx, gy] = GAUGE;
  out.push(S(k.cylinder(gx, gy, GAUGE_R, BOX_H, GAUGE_TOP - BOX_H, P, 48, 0.5), { tone: "hi", crease: "lo" }));
  out.push(L(k.ring(gx, gy, GAUGE_R - 1.6, GAUGE_TOP, P, 48), { tone: "lo" }));
  const start = -GAUGE_SWEEP / 2;
  const ticks = k.radialTicks(gx, gy, GAUGE_R - 2, 24, 4, GAUGE_TOP, [1, 2], P, start, GAUGE_SWEEP * (25 / 24));
  out.push(L(ticks.minor, { tone: "lo" }), L(ticks.major, { tone: "mid" }));
  out.push(L(k.ring(gx, gy, 1.1, GAUGE_TOP, P, 12), { tone: "mid" }));
  const [lx, ly] = LAMP;
  out.push(S(k.cylinder(lx, ly, 5, BOX_H, 1.4, P, 24), { tone: "lo" }));
  out.push(`<g data-part="lamp-off">${S(k.cylinder(lx, ly, 3.6, BOX_H + 1.4, LAMP_TOP - BOX_H - 1.4, P, 24), { tone: "mid" })}</g>`);
  out.push(`<g data-part="lamp-on" style="display:none">${S(k.cylinder(lx, ly, 3.6, BOX_H + 1.4, LAMP_TOP - BOX_H - 1.4, P, 24), { tone: "hi", lit: true })}${D([at([lx, ly, LAMP_TOP])], { size: 0.6, tone: "hi", pulse: true })}</g>`);
  const switchAt = [LAMP[0] + 4, BOX.y + BOX.d - 7];
  out.push(S(k.cylinder(switchAt[0], switchAt[1], 2.6, BOX_H, 3, P, 18), { tone: "mid" }));
  out.push(L(k.knurl(switchAt[0], switchAt[1], 2.6, BOX_H, BOX_H + 3, 16, P), { tone: "lo" }));
  out.push(L(k.lineOnTop([BOX.x + 6, BOX.y + BOX.d - 6], [BOX.x + 30, BOX.y + BOX.d - 6], BOX_H, P), { tone: "lo", free: true }));
  return out.join("");
}

function cable() {
  return k.wireSvg(at([BOX.x + BOX.w, BOX.y + 10, 8]), at([CHAMBER.x + 18, CHAMBER.y + CHAMBER.d, 28]), {
    via: [at([BOX.x + BOX.w + 10, BOX.y + 10, 8]), at([CHAMBER.x + 18, CHAMBER.y + CHAMBER.d + 12, 8])],
    tone: "lo",
  });
}

const gaugeNeedle = (pressure) => {
  const [gx, gy] = GAUGE;
  const share = Math.min(1, Math.max(0, (Math.log10(pressure) - LOG_P[0]) / (LOG_P[1] - LOG_P[0])));
  const angle = -GAUGE_SWEEP / 2 + share * GAUGE_SWEEP;
  return k.lineOnTop([gx - Math.sin(angle) * 2, gy + Math.cos(angle) * 2], [gx + Math.sin(angle) * (GAUGE_R - 2.6), gy - Math.cos(angle) * (GAUGE_R - 2.6)], GAUGE_TOP + 0.2, P);
};

// Painter's order: legs, the chamber shell, its contents, then the near walls and the slit valve.

const svg = k.figureSvg({
  width: WIDTH,
  height: HEIGHT,
  label:
    "Chamber1 of the simulated tool, lid removed: a slit-valve gate on the right wall slides down into its bonnet to open, three lift pins raise the wafer off the chuck, the chuck turns, and a control box shows chamber pressure on a log gauge with a vacuum lamp. Workflows move every part through Simulation Action nodes.",
  body: [
    plate(),
    `<path class="iso-halo" d="${k.haloOf(k.insetPlan(CHAMBER, 6), 0, 0.5, P)}" style="filter:blur(6px)"/>`,
    ...byDepth(LEGS).map(legSvg),
    floor(),
    wall(wallPlans.back),
    wall(wallPlans.left),
    pedestal(),
    `<g data-part="chuck-ticks">${chuckTicksAt(0)}</g>`,
    WAFER_SHADOW,
    `<g data-part="pins">${pinsAt(PIN_DOWN)}</g>`,
    `<g data-part="wafer">${waferAt(CHUCK_TOP, 0)}</g>`,
    wall(wallPlans.front),
    wall(wallPlans.right),
    slitValveFrame(),
    `<g data-part="gate">${gate()}</g>`,
    bonnet(),
    cable(),
    controlBox(),
    `<path data-part="needle" class="iso-line" data-tone="lit" data-free="start" d="${gaugeNeedle(760)}" style="stroke-width:1"/>`,
  ],
});

const live = `
const P = ${JSON.stringify(P)};
const CHUCK_TOP = ${CHUCK_TOP}, CHUCK_R = ${CHUCK_R}, WAFER_R = ${WAFER_R}, PIN_R = ${PIN_R}, PIN_ORBIT = ${PIN_ORBIT};
const PIN_ANGLES = ${JSON.stringify(PIN_ANGLES)}, PIN_DOWN = ${PIN_DOWN}, PIN_UP = ${PIN_UP}, GATE_TRAVEL = ${GATE_TRAVEL};
const GAUGE = ${JSON.stringify(GAUGE)}, GAUGE_R = ${GAUGE_R}, GAUGE_TOP = ${GAUGE_TOP}, GAUGE_SWEEP = ${GAUGE_SWEEP}, LOG_P = ${JSON.stringify(LOG_P)};
const S = (paths, style) => solidSvg(paths, style);
const L = (d, style) => lineSvg(d, style);
const depth = ([x, y, z = 0]) => depthOf([x, y, z], P);
const byDepth = (points) => [...points].sort((a, b) => depth(a) - depth(b));
const k = { cylinder, haloOf, ring, lineOnTop, radialTicks, iso };
const pinsAt = ${pinsAt.toString()};
const waferAt = ${waferAt.toString()};
const chuckTicksAt = ${chuckTicksAt.toString()};
const gaugeNeedle = ${gaugeNeedle.toString()};

const stage = document.querySelector(".iso-stage");
const svg = stage.querySelector("svg");
const part = (name) => svg.querySelector('[data-part="' + name + '"]');
const gate = part("gate"), pins = part("pins"), wafer = part("wafer"), ticks = part("chuck-ticks"), needle = part("needle");
const lampOn = part("lamp-on"), lampOff = part("lamp-off");
const readout = document.querySelector("[data-readout]");
const linkState = document.querySelector("[data-corner='hint']");
const still = matchMedia("(prefers-reduced-motion: reduce)");

// Targets come from workflow commands; the drawn state follows them with time constants close to real hardware.
const target = { door: 0, pins: 0, rpm: 0, vacuum: false };
const now = { door: 0, pins: 0, rpm: 0, angle: 0, logP: Math.log10(760) };
const DEFAULT_RPM = 300;
const VISIBLE_RPM_LIMIT = 90;

const ACTIONS = {
  "chamber1.door": { open: () => (target.door = 1), close: () => (target.door = 0) },
  "chamber1.liftpin": { up: () => (target.pins = 1), down: () => (target.pins = 0) },
  "chamber1.spin": { start: (value) => (target.rpm = Number(value) > 0 ? Number(value) : DEFAULT_RPM), stop: () => (target.rpm = 0) },
  "chamber1.vacuum": { on: () => (target.vacuum = true), off: () => (target.vacuum = false) },
};

function apply(command) {
  const action = ACTIONS[String(command.target).toLowerCase()]?.[String(command.action).toLowerCase()];
  if (!action) return console.warn("Unknown simulation command", command);
  action(command.value);
  run();
}

function draw() {
  gate.setAttribute("transform", translateAlong(P, [0, 0, -GATE_TRAVEL * now.door]));
  const tip = PIN_DOWN + (PIN_UP - PIN_DOWN) * now.pins;
  pins.innerHTML = pinsAt(tip);
  wafer.innerHTML = waferAt(Math.max(CHUCK_TOP, tip), now.angle);
  ticks.innerHTML = chuckTicksAt(now.angle);
  const pressure = Math.pow(10, now.logP);
  needle.setAttribute("d", gaugeNeedle(pressure));
  lampOn.style.display = target.vacuum ? "" : "none";
  lampOff.style.display = target.vacuum ? "none" : "";
  const door = now.door > 0.98 ? "open" : now.door < 0.02 ? "closed" : (target.door ? "opening" : "closing");
  const lift = now.pins > 0.98 ? "up" : now.pins < 0.02 ? "down" : "moving";
  const text = "door " + door + " · pins " + lift + " · " + Math.round(now.rpm) + " rpm · " + pressure.toExponential(1) + " Torr";
  if (readout.textContent !== text) readout.textContent = text;
}

let frame = 0, last = 0, visible = false;
const follow = (value, goal, tau, dt) => (still.matches ? goal : goal + (value - goal) * Math.exp(-dt / tau));
function tick(time) {
  const dt = last ? Math.min((time - last) / 1000, 1 / 20) : 1 / 60;
  last = time;
  now.door = follow(now.door, target.door, 0.45, dt);
  now.pins = follow(now.pins, target.pins, 0.35, dt);
  now.rpm = follow(now.rpm, target.rpm, 0.9, dt);
  now.logP = follow(now.logP, target.vacuum ? -3 : Math.log10(760), target.vacuum ? 1.4 : 0.6, dt);
  now.angle = (now.angle + Math.min(now.rpm, VISIBLE_RPM_LIMIT) / 60 * Math.PI * 2 * dt) % (Math.PI * 2);
  draw();
  const settled = Math.abs(now.door - target.door) < 1e-3 && Math.abs(now.pins - target.pins) < 1e-3
    && now.rpm < 0.5 && target.rpm === 0 && Math.abs(now.logP - (target.vacuum ? -3 : Math.log10(760))) < 1e-3;
  frame = visible && !settled ? requestAnimationFrame(tick) : 0;
}
function run() { if (!frame && visible) { last = 0; frame = requestAnimationFrame(tick); } }
new IntersectionObserver((entries) => { visible = entries[entries.length - 1].isIntersecting; if (visible) run(); }).observe(stage);

stage.addEventListener("keydown", (event) => {
  const keys = { d: ["Chamber1.Door", target.door ? "Close" : "Open"], p: ["Chamber1.LiftPin", target.pins ? "Down" : "Up"], s: ["Chamber1.Spin", target.rpm ? "Stop" : "Start"], v: ["Chamber1.Vacuum", target.vacuum ? "Off" : "On"] };
  const command = keys[event.key.toLowerCase()];
  if (!command) return;
  event.preventDefault();
  apply({ target: command[0], action: command[1], value: null });
});

function connect(delay = 500) {
  if (location.protocol === "file:") return;
  const socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/live");
  socket.addEventListener("open", () => { linkState.textContent = "Live from workflows · keys D P S V"; delay = 500; });
  socket.addEventListener("message", (message) => {
    const data = JSON.parse(message.data);
    if (data.type === "simState") data.commands.forEach(apply);
    if (data.type === "sim") apply(data.command);
  });
  socket.addEventListener("close", () => {
    linkState.textContent = "Simulator offline · keys D P S V";
    setTimeout(() => connect(Math.min(delay * 2, 8000)), delay);
  });
}

draw();
connect();
`;

const body = k.plateHtml({
  fig: "Chamber1",
  title: "Process chamber · lid removed",
  hint: "Connecting… · keys D P S V",
  readout: "door closed · pins down · 0 rpm · 7.6e+2 Torr",
  keys: [
    { mark: "lit", label: "Wafer: lifted by the pins, turned by the chuck" },
    { mark: "raised", label: "Slit-valve gate: slides down into its bonnet to open" },
    { mark: "edge", label: "Gauge needle: chamber pressure, 10³ to 10⁻³ Torr on a log scale" },
  ],
  caption:
    "Every motion here is commanded by a workflow's Simulation Action node (Chamber1.Door Open/Close, Chamber1.LiftPin Up/Down, Chamber1.Spin Start/Stop, Chamber1.Vacuum On/Off). The page only animates; PLC values change in the workflows.",
  body: `<div class="iso-stage" tabindex="0" aria-label="Chamber1 simulation. Keys: D door, P lift pins, S spin, V vacuum.">${svg}</div>`,
});

const file = theme === "light" ? "index-light.html" : "index.html";
writeFileSync(join(HERE, file), k.pageHtml({ title: "Chamber1 Simulation", theme, body, script: kitScript() + live, width: 720 }));
console.log("wrote", file);
