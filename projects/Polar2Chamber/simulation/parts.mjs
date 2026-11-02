import * as k from "./kit/iso-kit.mjs";

export const WIDTH = 760;
export const HEIGHT = 560;
export const VIEW = { pad: 24, azimuth: 50, elevation: 38 };
const RAD = Math.PI / 180;

export const PLATE = { x: -130, y: -130, w: 260, d: 260, r: 6 };
export const PLATE_H = 16;

export const WALL = 4;
export const SHELL_H = 118;
export const WALL_OPACITY = 0.5;
export const FFU = { x: -80, y: -80, w: 160, d: 160, r: 3 };
export const FFU_T = 5;
export const FFU_DOTS = { columns: 15, gap: 9.6 };
export const FFU_OPACITY = 0.7;

export const SLOT = { x0: -22, x1: 22, z0: 22, z1: 52 };
export const GATE = { x: -26, y: 130.4, w: 52, d: 3, r: 1 };
export const GATE_Z = 18;
export const GATE_H = 38;

export const HOUSING_R = 19;
export const HOUSING_H = 9;
export const CHUCK_R = 36;
export const CHUCK_TOP = 13;
export const PIN_R = 1.1;
export const PIN_ORBIT = 30;
export const PIN_COUNT = 6;
export const PIN_TOP = 16.5;
export const WAFER_R = 33;
export const WAFER_T = 1;
export const WAFER_TOP = PIN_TOP + WAFER_T;

export const RING = { inner: 36.8, outer: 39.8, base: HOUSING_H, top: 14 };
export const RING_TICKS = { count: 24, every: 6, lengths: [0.9, 1.8] };

export const CUP_OUTER_R = [50, 58, 66, 74];
export const CUP_WALL = 2.4;
export const CUP_LIP_REACH = 8;
export const CUP_LIP_RISE = 4;
export const CUP_LIP_T = 1.6;
export const CUP_STEPS = 72;
export const CUP_RIM = {
  Down: [3, 3, 3, 3],
  Middle: [16, 22, 28, 34],
  Top: [30, 40, 50, 60],
};
export const CUP_INITIAL = "Down";

export const CORNERS = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
export const POST_OFFSET = 105;
export const PIVOT_R = 8;
export const PIVOT_H = 14;
export const SHAFT_R = 3.2;
export const SHAFT_CAP = 4;
export const BOOM_R = 2.4;
export const HEAD_W = 7;
export const HEAD_BELOW = 5;
export const HEAD_ABOVE = 3;
export const HEAD_PAD = 4;
export const NOZZLE_R = 1.4;
export const NOZZLE_LEN = 5;
export const NOZZLE_COUNT = [3, 3, 2, 1];
export const NOZZLE_PITCH_DEG = 3.5;
export const ARM_Z = { Top: 90, Down: 72 };
export const ARM_LIFT_INITIAL = "Top";
export const ARM_ROTATE_INITIAL = "NozzlePort";
export const ARM_ROTATE_DEG = {
  NegLimit: -48,
  NozzlePort: -35,
  Noz3Center: -2 * NOZZLE_PITCH_DEG,
  Noz2Center: -NOZZLE_PITCH_DEG,
  Noz1Center: 0,
  PosLimit: 35,
};
export const ARM_ROTATE_NEEDS = { Noz1Center: 1, Noz2Center: 2, Noz3Center: 3 };

export const PORT_COUNT = [3, 3, 0, 0];
export const PORT_R = 12;
export const PORT_H = 5;
export const PORT_FLOOR = 2;
export const PORT_RIM_R = 10;
export const PORT_JET_H = 9;

export const JET_R = 1;

export const FIXED_ANGLE_DEG = [0, 90, 180, 270];
export const FIXED_R = 72;
export const FIXED_BODY = 2.6;
export const FIXED_BODY_H = 5;
export const FIXED_REACH = 7;
export const FIXED_LAND_R = 6;

export const EXHAUST_ANGLE_DEG = [0, 90, 180, 270];
export const EXHAUST_FROM_R = 118;
export const EXHAUST_TO_R = 144;
export const EXHAUST_Z = 10;
export const EXHAUST_PIPE_R = 7;
export const EXHAUST_FLOW_LEN = 34;
export const EXHAUST_FLOW_SPREAD = 3.5;

export const SPIN_DEG_PER_S = 90;
export const TWEEN_DEFAULT_MS = 1500;
export const ERROR_LINES = 20;
export const LABEL_LINE_PX = 10;

export const UNITS = { spin: "RPM", cup: "mm", lift: "mm", rotate: "deg", nozzle: "ml/s", port: "ml/s", fixed: "ml/s", exhaust: "Pa" };

export const P = k.fitProjection(
  [
    ...k.boxCorners(PLATE, -PLATE_H, 0),
    ...k.boxCorners(PLATE, 0, SHELL_H + FFU_T),
    ...[1, -1].flatMap((side) => [[side * (EXHAUST_TO_R + EXHAUST_FLOW_LEN), 0, EXHAUST_Z], [0, side * (EXHAUST_TO_R + EXHAUST_FLOW_LEN), EXHAUST_Z]]),
  ],
  WIDTH,
  HEIGHT,
  VIEW,
);

export const at = (point) => k.iso(point, P);
export const S = (paths, style) => k.solidSvg(paths, style);
export const L = (d, style) => k.lineSvg(d, style);
export const D = (points, style) => k.dotsSvg(points, style);
export const depth = ([x, y, z = 0]) => k.depthOf([x, y, z], P);
export const byDepth = (points) => [...points].sort((a, b) => depth(a) - depth(b));
export const polar = (cx, cy, radius, angle) => [cx + radius * Math.cos(angle), cy + radius * Math.sin(angle)];

export const rectRing = ([ax, ay], [bx, by], width) => {
  const length = Math.hypot(bx - ax, by - ay) || 1;
  const nx = (-(by - ay) / length) * (width / 2);
  const ny = ((bx - ax) / length) * (width / 2);
  return [[ax + nx, ay + ny], [bx + nx, by + ny], [bx - nx, by - ny], [ax - nx, ay - ny]];
};
export const solidRing = (corners, z, height, style) => S(k.extrude(corners, z, height, P, { convex: true }), style);

export const tubeSvg = (from, to, radius, extra = "") => {
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
        faces.push([centre, svg, faces.length]);
      }
    }
    const body = faces.sort((a, b) => (Math.abs(a[0] - b[0]) > 1e-6 ? a[0] - b[0] : a[2] - b[2])).map(([, svg]) => svg).join("");
    const lines = edges
      .filter((edge) => front || edge.both)
      .map((edge) => L(k.pathOf(thetas.map((theta) => at(vertex(edge.p, theta)))), { tone: edge.tone }))
      .join("");
    return `<g class="iso-solid" data-seamless>${body}${lines}</g>`;
  };
  return { back: half(false), front: half(true) };
}

export const slantHeight = (west, east, reach) => (x) => west + ((east - west) * (x + reach)) / (2 * reach);

export function cupParts(index, west, east) {
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
  return revolve(segments, slantHeight(west, east, outer), edges, CUP_STEPS);
}

export function outsideRingParts() {
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
  return revolve(segments, () => RING.top, edges, 48);
}

export function ringTicksSvg(angle) {
  const ticks = k.radialTicks(0, 0, RING.outer - 0.4, RING_TICKS.count, RING_TICKS.every, RING.top, RING_TICKS.lengths, P, angle);
  return L(ticks.minor, { tone: "lo" }) + L(ticks.major, { tone: "mid" });
}

export function holderSvg(angle) {
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
  return L(ticks.minor, { tone: "lo" }) + L(ticks.major, { tone: "mid" }) + pins + wafer;
}

export const armGeometry = (index) => {
  const [sx, sy] = CORNERS[index];
  const post = [sx * POST_OFFSET, sy * POST_OFFSET];
  return { post, length: Math.hypot(post[0], post[1]), toCenter: Math.atan2(-post[1], -post[0]) };
};

const boomAngle = (index, rotateDeg) => armGeometry(index).toCenter + rotateDeg * RAD;

export const nozzlePoints = (index, rotateDeg) => {
  const { post, length } = armGeometry(index);
  const boom = boomAngle(index, rotateDeg);
  return Array.from({ length: NOZZLE_COUNT[index] }, (_, nozzle) => polar(post[0], post[1], length, boom + nozzle * NOZZLE_PITCH_DEG * RAD));
};

export const headCentre = (points) => [points.reduce((sum, p) => sum + p[0], 0) / points.length, points.reduce((sum, p) => sum + p[1], 0) / points.length];

export const spanOf = (points, index, rotateDeg, pad) => {
  const boom = boomAngle(index, rotateDeg);
  const tangent = [-Math.sin(boom), Math.cos(boom)];
  const last = points[points.length - 1];
  return [[points[0][0] - tangent[0] * pad, points[0][1] - tangent[1] * pad], [last[0] + tangent[0] * pad, last[1] + tangent[1] * pad]];
};

export function armBodySvg(index, rotateDeg, liftZ) {
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

export const portCentre = (index) => headCentre(nozzlePoints(index, ARM_ROTATE_DEG.NozzlePort));

export const jetColumn = ([x, y], topZ, floorZ) => S(k.cylinder(x, y, JET_R, floorZ, topZ - floorZ, P, 10), { tone: "blue", crease: "none" });

function jetFloor(index, point) {
  if (Math.hypot(point[0], point[1]) <= WAFER_R) return WAFER_TOP;
  if (!PORT_COUNT[index]) return 0;
  const [cx, cy] = portCentre(index);
  return Math.hypot(point[0] - cx, point[1] - cy) <= PORT_RIM_R ? PORT_FLOOR : 0;
}

export function armJetSvg(index, nozzle, rotateDeg, liftZ) {
  const point = nozzlePoints(index, rotateDeg)[nozzle];
  return jetColumn(point, liftZ - HEAD_BELOW - NOZZLE_LEN, jetFloor(index, point));
}

export function fixedNozzleParts(index, cupWest, cupEast) {
  const angle = FIXED_ANGLE_DEG[index] * RAD;
  const [x, y] = polar(0, 0, FIXED_R, angle);
  const rim = slantHeight(cupWest, cupEast, CUP_OUTER_R[3])(x) + 1;
  const [tx, ty] = polar(0, 0, FIXED_R - FIXED_REACH, angle);
  const bodyPlan = { x: x - FIXED_BODY, y: y - FIXED_BODY, w: FIXED_BODY * 2, d: FIXED_BODY * 2, r: 0.8 };
  const tipZ = rim + FIXED_BODY_H - 1.5;
  const [lx, ly] = polar(0, 0, FIXED_LAND_R, angle);
  const base = S(k.slabOf(bodyPlan, rim, FIXED_BODY_H, P, 3, 0.3), { tone: "hi", crease: "lo" })
    + tubeSvg([x, y, tipZ], [tx, ty, tipZ], 0.8);
  const jet = `<path class="jet-line" d="${k.pathOf([at([tx, ty, tipZ]), at([lx, ly, WAFER_TOP])])}"/>`;
  return { base, jet };
}

function targetList() {
  const list = [];
  const add = (key, type, actions, extra = {}) => list.push({ key, type, unit: UNITS[type], actions, ...extra });
  add("Holder", "spin", ["CW", "CCW", "Stop"]);
  add("OutsideRing", "spin", ["CW", "CCW", "Stop"]);
  CUP_OUTER_R.forEach((_, index) => ["Master", "Slave"].forEach((side) => add(`Cup${index + 1}.${side}`, "cup", ["Top", "Middle", "Down", "Hold"], { index, side })));
  CORNERS.forEach((_, index) => {
    add(`Arm${index + 1}.Lift`, "lift", ["Top", "Down", "Hold"], { index });
    add(`Arm${index + 1}.Rotate`, "rotate", [...Object.keys(ARM_ROTATE_DEG), "Hold"], { index });
    for (let nozzle = 0; nozzle < NOZZLE_COUNT[index]; nozzle++) add(`Arm${index + 1}.Nozzle${nozzle + 1}`, "nozzle", ["On", "Off"], { index });
    for (let port = 0; port < PORT_COUNT[index]; port++) add(`Arm${index + 1}.Port${port + 1}`, "port", ["On", "Off"], { index });
  });
  FIXED_ANGLE_DEG.forEach((_, index) => add(`FixedNozzle${index + 1}`, "fixed", ["On", "Off"], { index }));
  EXHAUST_ANGLE_DEG.forEach((_, index) => add(`ExhaustFan${index + 1}`, "exhaust", ["On", "Off"], { index }));
  return list;
}
export const TARGETS = targetList();

const block = (id, anchor, align, lines, offset = [0, 0]) => ({ id, anchor: at(anchor).map((v, i) => v + offset[i]), align, lines });
const single = (key, prefix) => ({ keys: [key], prefix });

export function labelBlocks() {
  const armPost = (index) => [...armGeometry(index).post, ARM_Z.Top + SHAFT_CAP];
  const armAlign = ["start", "start", "start", "end"];
  const armLines = (index) => [
    single(`Arm${index + 1}.Lift`, "Lift"),
    single(`Arm${index + 1}.Rotate`, "Rot"),
    ...Array.from({ length: NOZZLE_COUNT[index] }, (_, n) => single(`Arm${index + 1}.Nozzle${n + 1}`, `N${n + 1}`)),
    ...Array.from({ length: PORT_COUNT[index] }, (_, n) => single(`Arm${index + 1}.Port${n + 1}`, `P${n + 1}`)),
  ];
  const fixedSpot = [[FIXED_R + 24, 0, 0], [0, FIXED_R + 24, 0], [-(FIXED_R + 24), 0, 0], [0, -(FIXED_R + 24), 0]];
  const sideAlign = ["start", "end", "end", "start"];
  const exhaustSpot = [[EXHAUST_TO_R + 6, 0, EXHAUST_Z + 14], [0, EXHAUST_TO_R + 6, EXHAUST_Z + 14], [-(EXHAUST_TO_R + 6), 0, EXHAUST_Z + 14], [0, -(EXHAUST_TO_R + 6), EXHAUST_Z + 14]];
  return [
    block("Holder", [-10, 100, 0], "end", [single("Holder", "Holder")]),
    block("OutsideRing", [10, 100, 0], "start", [single("OutsideRing", "Ring")]),
    block("Cups", [92, -64, 0], "start", CUP_OUTER_R.map((_, i) => ({ keys: [`Cup${i + 1}.Master`, `Cup${i + 1}.Slave`], prefix: `Cup${i + 1}` }))),
    ...CORNERS.map((_, i) => block(`Arm${i + 1}`, armPost(i), armAlign[i], armLines(i), [armAlign[i] === "end" ? -8 : 8, 0])),
    ...fixedSpot.map((spot, i) => block(`FixedNozzle${i + 1}`, spot, sideAlign[i], [single(`FixedNozzle${i + 1}`, `FN${i + 1}`)])),
    ...exhaustSpot.map((spot, i) => block(`ExhaustFan${i + 1}`, spot, sideAlign[i], [single(`ExhaustFan${i + 1}`, `Fan${i + 1}`)])),
  ];
}
