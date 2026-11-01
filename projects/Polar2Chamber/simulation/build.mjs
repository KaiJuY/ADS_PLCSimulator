import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import * as k from "./kit/iso-kit.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const flag = (name) => process.argv.find((arg) => arg.startsWith(`--${name}=`))?.slice(name.length + 3);
const theme = process.argv.includes("--light") ? "light" : "dark";
const showActive = process.argv.includes("--on");
const poseNames = (flag("pose") ?? "").split(",").filter(Boolean);
const outFile = flag("out") ? resolve(flag("out")) : join(HERE, "index.html");
if (poseNames.length && !flag("out")) throw new Error("--pose needs --out=<file> so index.html stays the initial pose");

const kitScript = () => readFileSync(join(HERE, "kit", "iso-kit.mjs"), "utf8")
  .replace(/^export /gm, "")
  .replace(/<\/(body|html)/gi, "<\\/$1");

const WIDTH = 760;
const HEIGHT = 560;
const VIEW = { pad: 24, azimuth: 50, elevation: 38 };
const RAD = Math.PI / 180;

const PLATE = { x: -130, y: -130, w: 260, d: 260, r: 6 };
const PLATE_H = 16;

const WALL = 4;
const SHELL_H = 118;
const WALL_OPACITY = 0.5;
const FFU = { x: -80, y: -80, w: 160, d: 160, r: 3 };
const FFU_T = 5;
const FFU_DOTS = { columns: 15, gap: 9.6 };
const FFU_OPACITY = 0.7;

const SLOT = { x0: -22, x1: 22, z0: 22, z1: 52 };
const GATE = { x: -26, y: 130.4, w: 52, d: 3, r: 1 };
const GATE_Z = 18;
const GATE_H = 38;

const HOUSING_R = 19;
const HOUSING_H = 9;
const CHUCK_R = 36;
const CHUCK_TOP = 13;
const PIN_R = 1.1;
const PIN_ORBIT = 30;
const PIN_COUNT = 6;
const PIN_TOP = 16.5;
const WAFER_R = 33;
const WAFER_T = 1;
const WAFER_TOP = PIN_TOP + WAFER_T;
const SPIN_TICK_ANGLE = 0;

const RING = { inner: 36.8, outer: 39.8, base: HOUSING_H, top: 14 };

const CUP_OUTER_R = [50, 58, 66, 74];
const CUP_WALL = 2.4;
const CUP_LIP_REACH = 8;
const CUP_LIP_RISE = 4;
const CUP_LIP_T = 1.6;
const CUP_STEPS = 72;
const CUP_RIM = {
  Down: [3, 3, 3, 3],
  Middle: [16, 22, 28, 34],
  Top: [30, 40, 50, 60],
};
const CUP_INITIAL = "Down";

const CORNERS = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
const POST_OFFSET = 105;
const PIVOT_R = 8;
const PIVOT_H = 14;
const SHAFT_R = 3.2;
const SHAFT_CAP = 4;
const BOOM_R = 2.4;
const HEAD_W = 7;
const HEAD_BELOW = 5;
const HEAD_ABOVE = 3;
const HEAD_PAD = 4;
const NOZZLE_R = 1.4;
const NOZZLE_LEN = 5;
const NOZZLE_COUNT = [3, 3, 2, 1];
const NOZZLE_PITCH_DEG = 3.5;
const ARM_Z = { Top: 90, Down: 72 };
const ARM_LIFT_INITIAL = "Top";
const ARM_ROTATE_INITIAL = "NozzlePort";
const ARM_ROTATE_DEG = {
  NegLimit: -48,
  NozzlePort: -35,
  Noz3Center: -2 * NOZZLE_PITCH_DEG,
  Noz2Center: -NOZZLE_PITCH_DEG,
  Noz1Center: 0,
  PosLimit: 35,
};
const ARM_ROTATE_NEEDS = { Noz1Center: 1, Noz2Center: 2, Noz3Center: 3 };

const PORT_COUNT = [3, 3, 0, 0];
const PORT_R = 12;
const PORT_H = 5;
const PORT_FLOOR = 2;
const PORT_RIM_R = 10;
const PORT_JET_H = 9;

const JET_R = 1;

const FIXED_ANGLE_DEG = [0, 90, 180, 270];
const FIXED_R = 72;
const FIXED_BODY = 2.6;
const FIXED_BODY_H = 5;
const FIXED_REACH = 7;
const FIXED_LAND_R = 6;

const EXHAUST_ANGLE_DEG = [0, 90, 180, 270];
const EXHAUST_FROM_R = 118;
const EXHAUST_TO_R = 144;
const EXHAUST_Z = 10;
const EXHAUST_PIPE_R = 7;
const EXHAUST_FLOW_LEN = 34;
const EXHAUST_FLOW_SPREAD = 3.5;

const P = k.fitProjection(
  [
    ...k.boxCorners(PLATE, -PLATE_H, 0),
    ...k.boxCorners(PLATE, 0, SHELL_H + FFU_T),
    ...[1, -1].flatMap((side) => [[side * (EXHAUST_TO_R + EXHAUST_FLOW_LEN), 0, EXHAUST_Z], [0, side * (EXHAUST_TO_R + EXHAUST_FLOW_LEN), EXHAUST_Z]]),
  ],
  WIDTH,
  HEIGHT,
  VIEW,
);

const at = (point) => k.iso(point, P);
const S = (paths, style) => k.solidSvg(paths, style);
const L = (d, style) => k.lineSvg(d, style);
const D = (points, style) => k.dotsSvg(points, style);
const depth = ([x, y, z = 0]) => k.depthOf([x, y, z], P);
const byDepth = (points) => [...points].sort((a, b) => depth(a) - depth(b));
const shownIf = (show) => (show ? "" : ' style="display:none"');
const polar = (cx, cy, radius, angle) => [cx + radius * Math.cos(angle), cy + radius * Math.sin(angle)];

const rectRing = ([ax, ay], [bx, by], width) => {
  const length = Math.hypot(bx - ax, by - ay) || 1;
  const nx = (-(by - ay) / length) * (width / 2);
  const ny = ((bx - ax) / length) * (width / 2);
  return [[ax + nx, ay + ny], [bx + nx, by + ny], [bx - nx, by - ny], [ax - nx, ay - ny]];
};
const solidRing = (ring, z, height, style) => S(k.extrude(ring, z, height, P, { convex: true }), style);

const tubeSvg = (from, to, radius, extra = "") => {
  const [a, b] = [at(from), at(to)];
  const width = 2 * radius * k.cameraOf(P).k;
  const line = (cls, w, shift = 0) => `<path class="${cls}" d="${k.pathOf([a, b])}" style="stroke-width:${w.toFixed(2)}"${shift ? ` transform="translate(0 ${shift.toFixed(2)})"` : ""}/>`;
  return `<g class="tube"${extra}>${line("tube-edge", width + 1.1)}${line("tube-body", width)}${line("tube-shade", width * 0.5, width * 0.25)}${line("tube-hi", width * 0.28, -width * 0.24)}</g>`;
};

const viewerAngle = VIEW.azimuth * RAD;
const viewVector = [Math.cos(viewerAngle) * Math.cos(VIEW.elevation * RAD), Math.sin(viewerAngle) * Math.cos(VIEW.elevation * RAD), Math.sin(VIEW.elevation * RAD)];
const lightOf = ([nx, ny]) => {
  const [lx, ly] = [-Math.sin(viewerAngle), Math.cos(viewerAngle)];
  return Math.min(k.SHADES - 1, Math.max(0, Math.floor(((nx * lx + ny * ly + 1) / 2) * k.SHADES)));
};

const pt = (r, z, abs = false) => ({ r, z, abs });
const seg = (a, b, n) => {
  const length = Math.hypot(n[0], n[1]);
  return { a, b, n: [n[0] / length, n[1] / length] };
};

function revolve(segments, zOf, edges, steps) {
  const half = (front) => {
    const count = steps / 2;
    const start = viewerAngle + (front ? -Math.PI / 2 : Math.PI / 2);
    const thetas = Array.from({ length: count + 1 }, (_, index) => start + (index / count) * Math.PI);
    const vertex = (p, theta) => {
      const [x, y] = polar(0, 0, p.r, theta);
      return [x, y, p.abs ? p.z : zOf(x) + p.z];
    };
    const faces = [];
    for (const s of segments) {
      for (let index = 0; index < count; index++) {
        const middle = (thetas[index] + thetas[index + 1]) / 2;
        const normal = [s.n[0] * Math.cos(middle), s.n[0] * Math.sin(middle), s.n[1]];
        if (normal[0] * viewVector[0] + normal[1] * viewVector[1] + normal[2] * viewVector[2] <= 0.02) continue;
        const quad = [vertex(s.a, thetas[index]), vertex(s.a, thetas[index + 1]), vertex(s.b, thetas[index + 1]), vertex(s.b, thetas[index])];
        const centre = quad.reduce((sum, point) => sum + depth(point), 0);
        const d = k.pathOf(quad.map(at), true);
        const svg = Math.abs(s.n[1]) > 0.5
          ? `<path class="iso-top" d="${d}"/>`
          : `<path class="iso-shade" data-shade="${lightOf(normal)}" d="${d}"/>`;
        faces.push([centre, svg]);
      }
    }
    const body = faces.sort((a, b) => a[0] - b[0]).map(([, svg]) => svg).join("");
    const lines = edges
      .filter((edge) => front || edge.both)
      .map((edge) => L(k.pathOf(thetas.map((theta) => at(vertex(edge.p, theta)))), { tone: edge.tone }))
      .join("");
    return `<g class="iso-solid" data-seamless>${body}${lines}</g>`;
  };
  return { back: half(false), front: half(true) };
}

const slantHeight = (west, east, reach) => (x) => west + ((east - west) * (x + reach)) / (2 * reach);

function cupParts(index, west, east) {
  const outer = CUP_OUTER_R[index];
  const inner = outer - CUP_WALL;
  const tip = outer - CUP_LIP_REACH;
  const segments = [
    seg(pt(outer, 0, true), pt(outer, 0), [1, 0]),
    seg(pt(outer, 0), pt(tip, CUP_LIP_RISE), [CUP_LIP_RISE, CUP_LIP_REACH]),
    seg(pt(tip, CUP_LIP_RISE), pt(tip, CUP_LIP_RISE - CUP_LIP_T), [-1, 0]),
    seg(pt(inner, -CUP_LIP_T), pt(inner, 0, true), [-1, 0]),
  ];
  const edges = [
    { p: pt(outer, 0), tone: "hi", both: true },
    { p: pt(tip, CUP_LIP_RISE), tone: "hi", both: true },
    { p: pt(tip, CUP_LIP_RISE - CUP_LIP_T), tone: "lo", both: false },
    { p: pt(outer, 0, true), tone: "lo", both: false },
  ];
  const halves = revolve(segments, slantHeight(west, east, outer), edges, CUP_STEPS);
  return {
    back: `<g id="Cup${index + 1}-back">${halves.back}</g>`,
    front: `<g id="Cup${index + 1}-front">${halves.front}</g>`,
  };
}

function outsideRingParts() {
  const flat = () => RING.top;
  const segments = [
    seg(pt(RING.outer, RING.base, true), pt(RING.outer, RING.top, true), [1, 0]),
    seg(pt(RING.outer, RING.top, true), pt(RING.inner, RING.top, true), [0, 1]),
    seg(pt(RING.inner, RING.top, true), pt(RING.inner, RING.base, true), [-1, 0]),
  ];
  const edges = [
    { p: pt(RING.outer, RING.top, true), tone: "hi", both: true },
    { p: pt(RING.inner, RING.top, true), tone: "lo", both: true },
    { p: pt(RING.outer, RING.base, true), tone: "lo", both: false },
  ];
  const halves = revolve(segments, flat, edges, 48);
  const ticks = k.radialTicks(0, 0, RING.outer - 0.4, 24, 6, RING.top, [0.9, 1.8], P, SPIN_TICK_ANGLE);
  return {
    back: `<g id="OutsideRing-back">${halves.back}</g>`,
    front: `<g id="OutsideRing-front">${halves.front}${L(ticks.minor, { tone: "lo" })}${L(ticks.major, { tone: "mid" })}</g>`,
  };
}

function motorHousingSvg() {
  return S(k.cylinder(0, 0, HOUSING_R + 6, 0, 1.6, P, 48), { tone: "lo" })
    + S(k.cylinder(0, 0, HOUSING_R, 1.6, HOUSING_H - 1.6, P, 48, 0.4), { tone: "mid", crease: "none" })
    + L(k.sideArc(0, 0, HOUSING_R, 5, P, 32), { tone: "faint" });
}

function chuckSvg() {
  return S(k.cylinder(0, 0, CHUCK_R, HOUSING_H, CHUCK_TOP - HOUSING_H, P, 72, 0.6), { tone: "hi", crease: "lo" })
    + L(k.sideArc(0, 0, CHUCK_R, HOUSING_H + 2, P, 56), { tone: "lo" });
}

function holderSvg(angle) {
  const pinAngles = Array.from({ length: PIN_COUNT }, (_, index) => angle + (index / PIN_COUNT) * Math.PI * 2);
  const pins = byDepth(pinAngles.map((a) => polar(0, 0, PIN_ORBIT, a)))
    .map(([x, y]) => S(k.cylinder(x, y, PIN_R, CHUCK_TOP, PIN_TOP - CHUCK_TOP, P, 12), { tone: "mid", crease: "none" })
      + S(k.cylinder(x, y, PIN_R + 0.5, PIN_TOP - 0.7, 0.7, P, 12), { tone: "hi", crease: "lo" })).join("");
  const ticks = k.radialTicks(0, 0, CHUCK_R - 1, 36, 6, CHUCK_TOP, [1, 2], P, angle);
  const notchFrom = polar(0, 0, WAFER_R - 6, angle);
  const notchTo = polar(0, 0, WAFER_R - 0.6, angle);
  const wafer = S(k.cylinder(0, 0, WAFER_R, PIN_TOP, WAFER_T, P, 72), { tone: "hi", lit: true, className: "wafer" })
    + L(k.ring(0, 0, WAFER_R - 2.5, WAFER_TOP, P, 72), { tone: "faint" })
    + L(k.lineOnTop(notchFrom, notchTo, WAFER_TOP, P), { tone: "lit", free: "start" });
  return `<g id="Holder">${L(ticks.minor, { tone: "lo" })}${L(ticks.major, { tone: "mid" })}${pins}${wafer}</g>`;
}

function plateSvg() {
  const halo = `<path class="iso-halo" d="${k.haloOf(PLATE, -PLATE_H, PLATE_H, P)}"/>`;
  const slab = S(k.slabOf(PLATE, -PLATE_H, PLATE_H, P, 8, 1.6), { tone: "mid" })
    + L(k.planOutline(k.insetPlan(PLATE, 5), 0, P), { tone: "lo" });
  const screws = k.corners(PLATE, 10);
  const dots = D(screws.map(([x, y]) => at([x, y, 0])), { size: 0.5 });
  return `<g id="Platform">${halo}${slab}${L(screws.map(([x, y]) => k.ring(x, y, 2.2, 0, P, 16)).join(""), { tone: "lo" })}${dots}</g>`;
}

const wallPlans = {
  back: { x: PLATE.x, y: PLATE.y, w: PLATE.w, d: WALL, r: 1 },
  left: { x: PLATE.x, y: PLATE.y + WALL, w: WALL, d: PLATE.d - 2 * WALL, r: 0.5 },
  front: { x: PLATE.x, y: PLATE.y + PLATE.d - WALL, w: PLATE.w, d: WALL, r: 1 },
  right: { x: PLATE.x + PLATE.w - WALL, y: PLATE.y + WALL, w: WALL, d: PLATE.d - 2 * WALL, r: 0.5 },
};

const wallSvg = (name) => S(k.slabOf(wallPlans[name], 0, SHELL_H, P, 3, 0.4), { tone: "mid", inner: L(k.sideSeam(wallPlans[name], SHELL_H - 8, P, 3), { tone: "faint" }) });
const wallsSvg = (names, id) => `<g id="${id}" style="opacity:${WALL_OPACITY}">${names.map(wallSvg).join("")}</g>`;

function slotAndGateSvg() {
  const y = PLATE.y + PLATE.d + 0.05;
  const slot = [[SLOT.x0, SLOT.z0], [SLOT.x1, SLOT.z0], [SLOT.x1, SLOT.z1], [SLOT.x0, SLOT.z1]].map(([x, z]) => at([x, y, z]));
  const frame = `<path d="${k.pathOf(slot, true)}" style="fill:var(--anatomy-shade-0);stroke:var(--anatomy-lo);stroke-width:var(--anatomy-weight)"/>`;
  const gate = S(k.slabOf(GATE, GATE_Z, GATE_H, P, 2, 0.4), { tone: "hi", crease: "lo" })
    + L(k.sideSeam(GATE, GATE_Z + GATE_H - 5, P, 2), { tone: "lo" })
    + D([[GATE.x + 4, GATE.y + GATE.d], [GATE.x + GATE.w - 4, GATE.y + GATE.d]].map(([x, yy]) => at([x, yy, GATE_Z + GATE_H - 8])), { size: 0.45 });
  return `${frame}<g id="Gate">${gate}</g>`;
}

function ffuSvg() {
  const slab = S(k.slabOf(FFU, SHELL_H, FFU_T, P, 4, 0.6), { tone: "mid" })
    + L(k.planOutline(k.insetPlan(FFU, 6), SHELL_H + FFU_T, P, 4), { tone: "lo" });
  const start = FFU.x + (FFU.w - (FFU_DOTS.columns - 1) * FFU_DOTS.gap) / 2;
  const grille = D(k.dotGrid(start, FFU.y + (FFU.d - (FFU_DOTS.columns - 1) * FFU_DOTS.gap) / 2, FFU_DOTS.columns, FFU_DOTS.columns, FFU_DOTS.gap, SHELL_H + FFU_T, P), { size: 0.5, tone: "hi" });
  return `<g id="FFU" style="opacity:${FFU_OPACITY}">${slab}${grille}</g>`;
}

const armGeometry = (index) => {
  const [sx, sy] = CORNERS[index];
  const post = [sx * POST_OFFSET, sy * POST_OFFSET];
  return { post, length: Math.hypot(post[0], post[1]), toCenter: Math.atan2(-post[1], -post[0]) };
};

const boomAngle = (index, rotateDeg) => armGeometry(index).toCenter + rotateDeg * RAD;

const nozzlePoints = (index, rotateDeg) => {
  const { post, length } = armGeometry(index);
  const boom = boomAngle(index, rotateDeg);
  return Array.from({ length: NOZZLE_COUNT[index] }, (_, nozzle) => polar(post[0], post[1], length, boom + nozzle * NOZZLE_PITCH_DEG * RAD));
};

const headCentre = (points) => [points.reduce((sum, p) => sum + p[0], 0) / points.length, points.reduce((sum, p) => sum + p[1], 0) / points.length];

const spanOf = (points, index, rotateDeg, pad) => {
  const boom = boomAngle(index, rotateDeg);
  const tangent = [-Math.sin(boom), Math.cos(boom)];
  const last = points[points.length - 1];
  return [[points[0][0] - tangent[0] * pad, points[0][1] - tangent[1] * pad], [last[0] + tangent[0] * pad, last[1] + tangent[1] * pad]];
};

function pivotSvg(index) {
  const [x, y] = armGeometry(index).post;
  return S(k.cylinder(x, y, PIVOT_R + 2.5, 0, 1.6, P, 28), { tone: "lo" })
    + S(k.cylinder(x, y, PIVOT_R, 1.6, PIVOT_H - 1.6, P, 28, 0.5), { tone: "mid", crease: "none" })
    + L(k.sideArc(x, y, PIVOT_R, 6, P, 20), { tone: "faint" })
    + L(k.ring(x, y, PIVOT_R - 2, PIVOT_H, P, 24), { tone: "lo" });
}

function armBodySvg(index, rotateDeg, liftZ) {
  const [px, py] = armGeometry(index).post;
  const points = nozzlePoints(index, rotateDeg);
  const [centreX, centreY] = headCentre(points);
  const [from, to] = spanOf(points, index, rotateDeg, HEAD_PAD);
  const headZ = liftZ - HEAD_BELOW;
  const out = [
    S(k.cylinder(px, py, SHAFT_R, PIVOT_H, liftZ + SHAFT_CAP - PIVOT_H, P, 20, 0.4), { tone: "hi", crease: "lo" }),
    tubeSvg([px, py, liftZ], [centreX, centreY, liftZ], BOOM_R),
  ];
  for (const [x, y] of byDepth(points)) out.push(S(k.cylinder(x, y, NOZZLE_R, headZ - NOZZLE_LEN, NOZZLE_LEN, P, 12), { tone: "hi", crease: "none" }));
  out.push(solidRing(rectRing(from, to, HEAD_W), headZ, HEAD_BELOW + HEAD_ABOVE, { tone: "hi", crease: "lo" }));
  return out.join("");
}

const portCentre = (index) => headCentre(nozzlePoints(index, ARM_ROTATE_DEG.NozzlePort));

const jetColumn = ([x, y], topZ, floorZ) => S(k.cylinder(x, y, JET_R, floorZ, topZ - floorZ, P, 10), { tone: "blue", crease: "none" });

function jetFloor(index, point) {
  if (Math.hypot(point[0], point[1]) <= WAFER_R) return WAFER_TOP;
  if (!PORT_COUNT[index]) return 0;
  const [cx, cy] = portCentre(index);
  return Math.hypot(point[0] - cx, point[1] - cy) <= PORT_RIM_R ? PORT_FLOOR : 0;
}

function armJetsSvg(index, rotateDeg, liftZ, on) {
  const tipZ = liftZ - HEAD_BELOW - NOZZLE_LEN;
  return nozzlePoints(index, rotateDeg).map((point, nozzle) => `<g id="Arm${index + 1}-Nozzle${nozzle + 1}-jet" class="liquid"${shownIf(on)}>${jetColumn(point, tipZ, jetFloor(index, point))}</g>`).join("");
}

function armSvg(index, rotateName, liftName, on) {
  const rotateDeg = ARM_ROTATE_DEG[rotateName];
  const liftZ = ARM_Z[liftName];
  return [
    `<g id="Arm${index + 1}-post">${pivotSvg(index)}</g>`,
    armJetsSvg(index, rotateDeg, liftZ, on),
    `<g id="Arm${index + 1}">${armBodySvg(index, rotateDeg, liftZ)}</g>`,
  ].join("");
}

function portSvg(index, on) {
  const count = PORT_COUNT[index];
  if (!count) return "";
  const [cx, cy] = portCentre(index);
  const points = nozzlePoints(index, ARM_ROTATE_DEG.NozzlePort).slice(0, count);
  const base = S(k.cylinder(cx, cy, PORT_R, 0, PORT_H, P, 40, 0.4), { tone: "mid", crease: "lo" })
    + L(k.ring(cx, cy, PORT_RIM_R, PORT_H, P, 40), { tone: "lo" })
    + L(k.ring(cx, cy, PORT_RIM_R - 3, PORT_FLOOR + 0.5, P, 32), { tone: "faint" });
  const jets = points.map((point, nozzle) => `<g id="Arm${index + 1}-Port${nozzle + 1}-jet" class="liquid"${shownIf(on)}>${jetColumn(point, PORT_FLOOR + PORT_JET_H, PORT_FLOOR)}</g>`).join("");
  return `<g id="Arm${index + 1}-PortBase">${base}</g>${jets}`;
}

function fixedNozzleSvg(index, cupWest, cupEast, on) {
  const angle = FIXED_ANGLE_DEG[index] * RAD;
  const [x, y] = polar(0, 0, FIXED_R, angle);
  const rim = slantHeight(cupWest, cupEast, CUP_OUTER_R[3])(x) + 1;
  const [tx, ty] = polar(0, 0, FIXED_R - FIXED_REACH, angle);
  const bodyPlan = { x: x - FIXED_BODY, y: y - FIXED_BODY, w: FIXED_BODY * 2, d: FIXED_BODY * 2, r: 0.8 };
  const tipZ = rim + FIXED_BODY_H - 1.5;
  const [lx, ly] = polar(0, 0, FIXED_LAND_R, angle);
  const body = S(k.slabOf(bodyPlan, rim, FIXED_BODY_H, P, 3, 0.3), { tone: "hi", crease: "lo" })
    + tubeSvg([x, y, rim + FIXED_BODY_H - 1.5], [tx, ty, tipZ], 0.8);
  const jet = `<path class="jet-line" d="${k.pathOf([at([tx, ty, tipZ]), at([lx, ly, WAFER_TOP])])}"/>`;
  return `<g id="FixedNozzle${index + 1}-base">${body}</g><g id="FixedNozzle${index + 1}-jet" class="liquid"${shownIf(on)}>${jet}</g>`;
}

function exhaustSvg(index, on) {
  const angle = EXHAUST_ANGLE_DEG[index] * RAD;
  const [dx, dy] = [Math.round(Math.cos(angle)), Math.round(Math.sin(angle))];
  const axis = dx ? "x" : "y";
  const from = [dx * EXHAUST_FROM_R, dy * EXHAUST_FROM_R, EXHAUST_Z];
  const to = [dx * EXHAUST_TO_R, dy * EXHAUST_TO_R, EXHAUST_Z];
  const rings = L(k.sideRing(to, EXHAUST_PIPE_R, axis, P, 28) + k.sideRing(to, EXHAUST_PIPE_R - 2.2, axis, P, 28), { tone: "mid" });
  const flow = [-1, 0, 1].map((offset) => {
    const lateral = axis === "x" ? [0, offset * EXHAUST_FLOW_SPREAD] : [offset * EXHAUST_FLOW_SPREAD, 0];
    const start = [to[0] + dx * 3 + lateral[0], to[1] + dy * 3 + lateral[1], EXHAUST_Z];
    const end = [to[0] + dx * (3 + EXHAUST_FLOW_LEN) + lateral[0], to[1] + dy * (3 + EXHAUST_FLOW_LEN) + lateral[1], EXHAUST_Z];
    return L(k.pathOf([at(start), at(end)]), { tone: "blue", flow: true });
  }).join("");
  return `<g id="ExhaustFan${index + 1}">${tubeSvg(from, to, EXHAUST_PIPE_R)}${rings}<g id="ExhaustFan${index + 1}-flow"${shownIf(on)}>${flow}</g></g>`;
}

const armsByDepth = [0, 1, 2, 3].sort((a, b) => depth([...armGeometry(a).post, 0]) - depth([...armGeometry(b).post, 0]));

function buildPose() {
  const named = (name) => poseNames.includes(name);
  const rotate = poseNames.find((name) => name in ARM_ROTATE_DEG) ?? ARM_ROTATE_INITIAL;
  const cupName = ["Down", "Middle", "Top"].find(named) ?? CUP_INITIAL;
  const armLift = ["Down", "Top"].find(named) ?? ARM_LIFT_INITIAL;
  const cupNames = named("sketch") ? ["Down", "Down", "Top", "Top"] : Array(4).fill(cupName);
  return { rotate, armLift, cupNames };
}

function build() {
  const pose = buildPose();
  const cupRim = pose.cupNames.map((name, index) => CUP_RIM[name][index]);
  const cups = cupRim.map((rim, index) => cupParts(index, rim, rim));
  const ring = outsideRingParts();
  const arms = armsByDepth.map((index) => {
    const rotateName = (ARM_ROTATE_NEEDS[pose.rotate] ?? 0) > NOZZLE_COUNT[index] ? ARM_ROTATE_INITIAL : pose.rotate;
    return armSvg(index, rotateName, pose.armLift, showActive);
  });
  const all = [0, 1, 2, 3];
  const body = [
    plateSvg(),
    wallsSvg(["back", "left", "front", "right"], "Walls"),
    ...[2, 3].map((index) => exhaustSvg(index, showActive)),
    ...all.map((index) => portSvg(index, showActive)),
    motorHousingSvg(),
    ...[3, 2, 1, 0].map((index) => cups[index].back),
    ring.back,
    chuckSvg(),
    holderSvg(SPIN_TICK_ANGLE),
    ring.front,
    ...all.map((index) => cups[index].front),
    ...all.map((index) => fixedNozzleSvg(index, cupRim[3], cupRim[3], showActive)),
    ...arms,
    slotAndGateSvg(),
    ...[0, 1].map((index) => exhaustSvg(index, showActive)),
    ffuSvg(),
  ];
  return k.figureSvg({
    width: WIDTH,
    height: HEIGHT,
    label: "Polar2 single-wafer wet-clean chamber: a spin chuck with a wafer, four nested splash-guard cups with conical lips, four swing dispense arms with nozzle heads, nozzle ports, fixed nozzles on the outer cup, four exhaust ducts, a wafer slot with its gate and a filter unit on top.",
    body,
  });
}

const PAGE_EXTRA_CSS = [
  ".tube path{fill:none;stroke-linecap:round;vector-effect:none}",
  ".tube-edge{stroke:var(--anatomy-hi)}",
  ".tube-body{stroke:var(--anatomy-shade-3)}",
  ".tube-shade{stroke:var(--anatomy-shade-0)}",
  ".tube-hi{stroke:var(--anatomy-top)}",
  ".wafer{--anatomy-lit-top:#1d2a3a;--anatomy-lit-shade:#17222f}",
  ".iso[data-theme=\"light\"] .wafer{--anatomy-lit-top:#e8f0fb;--anatomy-lit-shade:#dbe6f4}",
  ".liquid .iso-fill{fill:rgba(107,155,255,.35)}",
  ".liquid .iso-shade{fill:rgba(107,155,255,.45)}",
  ".liquid .iso-top{fill:rgba(160,190,255,.6)}",
  ".jet-line{fill:none;stroke:var(--anatomy-blue);stroke-opacity:.7;stroke-width:2.2;stroke-linecap:round;vector-effect:none}",
].join("");

const runtime = `
const P = ${JSON.stringify(P)};
`;

const page = k.plateHtml({
  fig: "Polar2",
  title: "Polar2 chamber",
  hint: "Static drawing",
  readout: "",
  keys: [
    { mark: "lit", label: "Wafer on the spin chuck" },
    { mark: "raised", label: "Cup1 (inner) to Cup4 (outer): wall and conical lip" },
    { mark: "blue", label: "Liquid and exhaust flow, shown only while active" },
  ],
  caption: "Initial pose: Cups Down, Arms at NozzlePort and Top, Holder and OutsideRing stopped, no liquid, no exhaust flow.",
  body: `<style>${PAGE_EXTRA_CSS}</style><div class="iso-stage">${build()}</div>`,
});

writeFileSync(outFile, k.pageHtml({ title: "Polar2 Simulation", theme, body: page, script: kitScript() + runtime, width: 760 }));
console.log("wrote", outFile);
