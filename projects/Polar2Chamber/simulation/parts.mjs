import * as k from "./kit/iso-kit.mjs";

export const WIDTH = 760;
export const HEIGHT = 560;
export const VIEW = { pad: 24, azimuth: 50, elevation: 38 };
const RAD = Math.PI / 180;

export const PLATE = { x: -130, y: -130, w: 260, d: 260, r: 6 };
export const PLATE_H = 16;
export const PLATE_OPACITY = 0.6;

export const WALL = 4;
export const SHELL_H = 118;
export const WALL_OPACITY = 0.5;

export const SLOT = { x0: -22, x1: 22, z0: 22, z1: 52 };
export const GATE = { x: -26, y: 130.4, w: 52, d: 3, r: 1 };
export const GATE_Z = 18;
export const GATE_H = 38;

export const HOUSING_R = 19;
export const HOUSING_H = 9;
export const CHUCK_R = 36;
export const CHUCK_TOP = 13;
export const PIN_TOP = 16.5;
export const WAFER_R = 33;
export const WAFER_T = 1;
export const WAFER_TOP = PIN_TOP + WAFER_T;

export const RING = { inner: 36.8, outer: 39.8, base: HOUSING_H, top: 14 };
export const RING_TICKS = { count: 24, every: 6, lengths: [0.9, 1.8] };

export const CUP_OUTER_R = [50, 58, 66, 74];
export const CUP_WALL = 2.4;
export const OUTERMOST_WALL_R = CUP_OUTER_R[CUP_OUTER_R.length - 1];
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
export const FIXED_BODY = 2.6;
export const FIXED_BODY_H = 5;
export const FIXED_REACH = 7;
export const FIXED_LAND_R = 6;
export const FIXED_WALL = { r: PLATE.w / 2 - WALL - 3, z: SHELL_H - 20 };

export const EXHAUST_ANGLE_DEG = [0, 90, 180, 270];
export const EXHAUST_FLOW_LEN = 34;
export const EXHAUST_FLOW_SPREAD = 9;

export const BASE_H = 46;
export const BASE_Z = -PLATE_H - BASE_H;
export const BASE_FLOOR = 2;
export const VACUUM_PAD = { r: 10, top: PIN_TOP };
export const CLAMP = { count: 6, orbit: WAFER_R + 2, r: 2, reach: 5, openDeg: 90 };
export const GRIPPER_GOAL = { Open: 0, Close: 1 };
export const GRIPPER_INITIAL = "Open";
export const RING_LIFT = { Down: 0, Top: 8 };
export const RING_LIFT_INITIAL = "Down";
export const SHUTTER_Z = { Close: GATE_Z, Open: SLOT.z1 + 4 };
export const SHUTTER_INITIAL = "Close";
export const VENT = { w: 70, h: 26, slats: 6, maxDeg: 75 };
export const VENT_GOAL = { Open: 1, Close: 0 };
export const VENT_INITIAL = "Close";
export const VENT_Z = BASE_Z + BASE_H / 2;
export const FAN = { r: 14, blades: 5, inset: 30, degPerS: 360, depth: 8 };
export const DAMPER = { pipeR: 5, drop: 18, branchLen: 34, flapLen: 9 };
export const DAMPER_Z = BASE_Z - DAMPER.drop;
export const DAMPER_GOAL = { AEX: 0, SEX: 1 };
export const DAMPER_INITIAL = "AEX";
export const ringLiftTransform = (lift) => `translate(0 ${(at([0, 0, lift])[1] - at([0, 0, 0])[1]).toFixed(2)})`;
export const JOG = { rotateDegPerS: 10, cupMmPerS: 6, rotateStepDeg: 5, cupStepMm: 4 };
export const JOG_ACTIONS = ["JogPositive", "JogNegative", "IncrementalJog", "StepDone"];
export const ACTION_UNITS = {
  rotate: { JogPositive: "RPM", JogNegative: "RPM" },
  cup: { JogPositive: "mm/s", JogNegative: "mm/s" },
};
export const STORAGE_PREFIX = "polar2-sim:";
export const SPLASH = {
  lowRpm: 300, highRpm: 1000, periodMs: 600, z: WAFER_TOP + 0.5,
  levels: { low: { drops: 8, length: 6, reach: 0.5 }, mid: { drops: 16, length: 10, reach: 0.8 }, high: { drops: 32, length: 16, reach: 1 } },
};

export const SPIN_DEG_PER_S = 90;
export const TWEEN_DEFAULT_MS = 1500;
export const ERROR_LINES = 20;
export const LABEL_LINE_PX = 10;

const FACE_R = PLATE.w / 2;
const FLOW_GAP = 3;

export const UNITS = {
  spin: "RPM", cup: "mm", lift: "mm", rotate: "deg", nozzle: "ml/s", port: "ml/s", fixed: "ml/s", exhaust: "Pa",
  gripper: "", ringLift: "", shutter: "", vent: "", damper: "", drain: "",
};

export const P = k.fitProjection(
  [
    ...k.boxCorners(PLATE, BASE_Z, SHELL_H),
    [0, 0, DAMPER_Z - DAMPER.pipeR],
    [DAMPER.branchLen, 0, DAMPER_Z - DAMPER.pipeR],
    [0, DAMPER.branchLen, DAMPER_Z - DAMPER.pipeR],
    ...[1, -1].flatMap((side) => {
      const reach = side * (FACE_R + FLOW_GAP + EXHAUST_FLOW_LEN);
      return [[reach, 0, VENT_Z], [0, reach, VENT_Z]];
    }),
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

const flat = (points, shade, tone = "lo") => {
  const d = k.pathOf(points.map(at), true);
  return `<g class="iso-solid"><path class="iso-fill" d="${d}"/><path class="iso-shade" data-shade="${shade}" d="${d}"/><path class="iso-line iso-edge" data-tone="${tone}" d="${d}"/></g>`;
};
const disc = (cx, cy, radius, z, steps = 24) => k.circleRing(cx, cy, radius, steps).map(([x, y]) => [x, y, z]);

const faceOf = (index) => {
  const angle = EXHAUST_ANGLE_DEG[index] * RAD;
  const u = [Math.round(Math.cos(angle)), Math.round(Math.sin(angle))];
  return { u, t: [-u[1], u[0]] };
};
const onFace = (index, radius, along, z) => {
  const { u, t } = faceOf(index);
  return [u[0] * radius + t[0] * along, u[1] * radius + t[1] * along, z];
};
const facesViewer = (u) => u[0] * Math.cos(viewerAngle) + u[1] * Math.sin(viewerAngle) > 0;

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

const CLAMP_HEAD_H = 1;

export function holderSvg(angle, clamp) {
  const open = (1 - clamp) * CLAMP.openDeg * RAD;
  const spots = Array.from({ length: CLAMP.count }, (_, index) => angle + (index / CLAMP.count) * Math.PI * 2).map((a) => ({ a, at: polar(0, 0, CLAMP.orbit, a) }));
  const sorted = [...spots].sort((a, b) => depth([...a.at, 0]) - depth([...b.at, 0]));
  const posts = sorted.map(({ at: [x, y] }) => S(k.cylinder(x, y, CLAMP.r, CHUCK_TOP, WAFER_TOP - CHUCK_TOP, P, 12), { tone: "mid", crease: "none" })).join("");
  const heads = sorted.map(({ a, at: [x, y] }) => {
    const aim = a + Math.PI + open;
    const tip = polar(x, y, CLAMP.reach, aim);
    return solidRing(rectRing([x, y], tip, CLAMP.r * 1.8), WAFER_TOP, CLAMP_HEAD_H, { tone: "hi", crease: "lo" })
      + S(k.cylinder(x, y, CLAMP.r + 0.3, WAFER_TOP + CLAMP_HEAD_H, 0.6, P, 12), { tone: "hi", crease: "lo" });
  }).join("");
  const ticks = k.radialTicks(0, 0, CHUCK_R - 1, 36, 6, CHUCK_TOP, [1, 2], P, angle);
  const pad = S(k.cylinder(0, 0, VACUUM_PAD.r, CHUCK_TOP, VACUUM_PAD.top - CHUCK_TOP, P, 32), { tone: "mid", crease: "none" });
  const notchFrom = polar(0, 0, WAFER_R - 6, angle);
  const notchTo = polar(0, 0, WAFER_R - 0.6, angle);
  const wafer = S(k.cylinder(0, 0, WAFER_R, PIN_TOP, WAFER_T, P, 72), { tone: "hi", lit: true, className: "wafer" })
    + L(k.ring(0, 0, WAFER_R - 2.5, WAFER_TOP, P, 72), { tone: "faint" })
    + L(k.lineOnTop(notchFrom, notchTo, WAFER_TOP, P), { tone: "lit", free: "start" });
  const hidden = L(k.ring(0, 0, VACUUM_PAD.r, WAFER_TOP, P, 32), { tone: "faint", dashed: true })
    + L(k.ring(0, 0, VACUUM_PAD.r / 2, WAFER_TOP, P, 24), { tone: "faint", dashed: true })
    + D(Array.from({ length: 6 }, (_, index) => at([...polar(0, 0, VACUUM_PAD.r * 0.75, angle + (index / 6) * Math.PI * 2), WAFER_TOP])), { size: 0.5, tone: "lo" });
  return L(ticks.minor, { tone: "lo" }) + L(ticks.major, { tone: "mid" }) + pad + posts + wafer + hidden + heads;
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

export function portCupSvg(index) {
  const [cx, cy] = portCentre(index);
  const body = S(k.cylinder(cx, cy, PORT_R, 0, PORT_H, P, 40, 0.4), { tone: "mid", crease: "lo" });
  const mouth = flat(disc(cx, cy, PORT_RIM_R, PORT_H, 40), 0, "lo");
  const floor = flat(disc(cx, cy, PORT_RIM_R - 3.5, PORT_FLOOR + 1, 32), 1, "faint");
  return body + mouth + floor + L(k.ring(cx, cy, PORT_R - 0.8, PORT_H, P, 40), { tone: "hi" });
}

export function fixedNozzleParts(index) {
  const angle = FIXED_ANGLE_DEG[index] * RAD;
  const [ux, uy] = [Math.round(Math.cos(angle)), Math.round(Math.sin(angle))];
  const [x, y] = polar(0, 0, FIXED_WALL.r, angle);
  const [tx, ty] = polar(0, 0, FIXED_WALL.r - FIXED_REACH, angle);
  const z = FIXED_WALL.z;
  const tipZ = z + FIXED_BODY_H - 1.5;
  const wallAt = FACE_R - WALL;
  const bracket = ux
    ? { x: ux * wallAt - 1 + (ux > 0 ? -0.5 : 0.5), y: -5, w: 2, d: 10, r: 0.5 }
    : { x: -5, y: uy * wallAt - 1 + (uy > 0 ? -0.5 : 0.5), w: 10, d: 2, r: 0.5 };
  const bodyPlan = { x: x - FIXED_BODY, y: y - FIXED_BODY, w: FIXED_BODY * 2, d: FIXED_BODY * 2, r: 0.8 };
  const [lx, ly] = polar(0, 0, FIXED_LAND_R, angle);
  const base = S(k.slabOf(bracket, z - 2, FIXED_BODY_H + 4, P, 2, 0.3), { tone: "mid", crease: "lo" })
    + S(k.slabOf(bodyPlan, z, FIXED_BODY_H, P, 3, 0.3), { tone: "hi", crease: "lo" })
    + tubeSvg([x, y, tipZ], [tx, ty, tipZ], 0.8);
  const samples = Array.from({ length: 17 }, (_, step) => {
    const s = step / 16;
    return at([tx + (lx - tx) * s, ty + (ly - ty) * s, tipZ + (WAFER_TOP - tipZ) * s * s]);
  });
  const jet = `<path class="jet-line" d="${k.pathOf(samples)}"/>`;
  return { base, jet };
}

const BASE_WALLS = {
  back: { x: PLATE.x, y: PLATE.y, w: PLATE.w, d: WALL, r: 1 },
  left: { x: PLATE.x, y: PLATE.y + WALL, w: WALL, d: PLATE.d - 2 * WALL, r: 0.5 },
  front: { x: PLATE.x, y: PLATE.y + PLATE.d - WALL, w: PLATE.w, d: WALL, r: 1 },
  right: { x: PLATE.x + PLATE.w - WALL, y: PLATE.y + WALL, w: WALL, d: PLATE.d - 2 * WALL, r: 0.5 },
};

export function baseSvg() {
  const floor = S(k.slabOf(PLATE, BASE_Z, BASE_FLOOR, P, 6, 0.4), { tone: "lo" });
  const walls = ["back", "left", "front", "right"]
    .map((name) => S(k.slabOf(BASE_WALLS[name], BASE_Z, BASE_H, P, 3, 0.4), { tone: "mid", inner: L(k.sideSeam(BASE_WALLS[name], BASE_Z + BASE_H - 6, P, 3), { tone: "faint" }) }))
    .join("");
  return floor + walls;
}

export function ventSvg(index, open) {
  const { u } = faceOf(index);
  const face = FACE_R + 0.3;
  const half = VENT.w / 2;
  const pitch = VENT.h / VENT.slats;
  const chord = pitch * 1.5;
  const theta = open * VENT.maxDeg * RAD;
  const shade = Math.min(k.SHADES - 1, lightOf(u) + Math.round(open * 2));
  const margin = 2;
  const top = VENT_Z + VENT.h / 2 + margin;
  const bottom = VENT_Z - VENT.h / 2 - margin;
  const frame = flat([onFace(index, face, -half - margin, bottom), onFace(index, face, half + margin, bottom), onFace(index, face, half + margin, top), onFace(index, face, -half - margin, top)], 0, "mid");
  const slats = Array.from({ length: VENT.slats }, (_, slat) => {
    const z = VENT_Z - VENT.h / 2 + (slat + 0.5) * pitch;
    const [dn, dz] = [(chord / 2) * Math.sin(theta), (chord / 2) * Math.cos(theta)];
    return flat([onFace(index, face + dn, -half + 1, z + dz), onFace(index, face + dn, half - 1, z + dz), onFace(index, face - dn, half - 1, z - dz), onFace(index, face - dn, -half + 1, z - dz)], shade, "mid");
  }).join("");
  const rod = L(k.pathOf([at(onFace(index, face + 0.4, half - 3, bottom + 1)), at(onFace(index, face + 0.4, half - 3, top - 1))]), { tone: "hi" });
  return frame + slats + rod;
}

export function fanSvg(index, angle) {
  const { u } = faceOf(index);
  const centre = FACE_R - FAN.inset;
  const half = FAN.r + 2;
  const front = facesViewer(u);
  const plane = centre + (front ? 1 : -1) * (FAN.depth / 2);
  const planePoint = (rho, phi) => {
    const { t } = faceOf(index);
    return [u[0] * plane + t[0] * rho * Math.cos(phi), u[1] * plane + t[1] * rho * Math.cos(phi), VENT_Z + rho * Math.sin(phi)];
  };
  const [cx, cy] = [u[0] * centre, u[1] * centre];
  const housing = S(k.slabOf(
    { x: cx - (u[0] ? FAN.depth / 2 : half), y: cy - (u[1] ? FAN.depth / 2 : half), w: u[0] ? FAN.depth : half * 2, d: u[1] ? FAN.depth : half * 2, r: 1.5 },
    VENT_Z - half, half * 2, P, 3, 0.4,
  ), { tone: "hi", crease: "lo" });
  const duct = tubeSvg(onFace(index, centre + FAN.depth / 2, 0, VENT_Z), onFace(index, FACE_R - WALL, 0, VENT_Z), FAN.r - 2);
  const blades = Array.from({ length: FAN.blades }, (_, blade) => {
    const phi = angle + (blade / FAN.blades) * Math.PI * 2;
    return flat([planePoint(3, phi), planePoint(3, phi + 0.7), planePoint(FAN.r - 1.5, phi + 1.2), planePoint(FAN.r - 1.5, phi + 0.5)], 2, "mid");
  }).join("");
  const bore = flat(Array.from({ length: 24 }, (_, step) => planePoint(FAN.r - 0.5, (step / 24) * Math.PI * 2)), 0, "lo");
  const hub = flat(Array.from({ length: 12 }, (_, step) => planePoint(3.2, (step / 12) * Math.PI * 2)), 3, "hi");
  return (front ? housing + duct : duct + housing) + bore + blades + hub;
}

export function damperSvg(position) {
  const r = DAMPER.pipeR;
  const flange = (centre, axis) => L(k.sideRing(centre, r + 1.5, axis, P, 24), { tone: "mid" });
  const swing = (1 - position) * (Math.PI / 2);
  const hinge = [0, 0, DAMPER_Z];
  const tip = [DAMPER.flapLen * Math.cos(swing), DAMPER.flapLen * Math.sin(swing)];
  const flap = flat([[0, 0, DAMPER_Z - r], [tip[0], tip[1], DAMPER_Z - r], [tip[0], tip[1], DAMPER_Z + r], [0, 0, DAMPER_Z + r]], 3, "hi");
  const actuator = tubeSvg([-24, 0, DAMPER_Z], [-9, 0, DAMPER_Z], 3.5)
    + tubeSvg([-9, 0, DAMPER_Z], [-2, 0, DAMPER_Z], 0.9)
    + L(k.sideRing([-24, 0, DAMPER_Z], 3.5, "x", P, 16) + k.sideRing([-9, 0, DAMPER_Z], 3.5, "x", P, 16), { tone: "mid" });
  const labels = (text, point) => `<text class="part-label" text-anchor="middle" x="${at(point)[0].toFixed(1)}" y="${(at(point)[1] + 10).toFixed(1)}"><tspan class="lbl-key">${text}</tspan></text>`;
  return actuator
    + tubeSvg([0, 0, BASE_Z], [0, 0, DAMPER_Z], r)
    + L(k.ring(0, 0, r + 2, BASE_Z, P, 20), { tone: "mid" })
    + tubeSvg([0, 0, DAMPER_Z], [DAMPER.branchLen, 0, DAMPER_Z], r)
    + tubeSvg([0, 0, DAMPER_Z], [0, DAMPER.branchLen, DAMPER_Z], r)
    + flange([DAMPER.branchLen, 0, DAMPER_Z], "x") + flange([0, DAMPER.branchLen, DAMPER_Z], "y")
    + flap
    + S(k.cylinder(hinge[0], hinge[1], 1.2, DAMPER_Z - r - 2, 2 * r + 4, P, 10), { tone: "hi", crease: "none" })
    + labels("AEX", [DAMPER.branchLen, 0, DAMPER_Z - r]) + labels("SEX", [0, DAMPER.branchLen, DAMPER_Z - r]);
}

export function shutterSvg(z) {
  return S(k.slabOf(GATE, z, GATE_H, P, 2, 0.4), { tone: "hi", crease: "lo" })
    + L(k.sideSeam(GATE, z + GATE_H - 5, P, 2), { tone: "lo" })
    + D([[GATE.x + 4, GATE.y + GATE.d], [GATE.x + GATE.w - 4, GATE.y + GATE.d]].map(([x, y]) => at([x, y, z + GATE_H - 8])), { size: 0.45 });
}

export function exhaustFlowSvg(index) {
  const lines = [-1, 0, 1].map((slot) => {
    const along = slot * EXHAUST_FLOW_SPREAD;
    return L(k.pathOf([at(onFace(index, FACE_R + FLOW_GAP, along, VENT_Z)), at(onFace(index, FACE_R + FLOW_GAP + EXHAUST_FLOW_LEN, along, VENT_Z))]), { tone: "blue", flow: true });
  });
  return lines.join("");
}

export function splashLevel(rpm) {
  const number = Number(rpm);
  if (!number) return "mid";
  if (number < SPLASH.lowRpm) return "low";
  return number > SPLASH.highRpm ? "high" : "mid";
}

export function splashDrops(angle, level, stopR, now) {
  const { drops, length, reach } = SPLASH.levels[level];
  return Array.from({ length: drops }, (_, index) => {
    const phase = (now / SPLASH.periodMs + index / drops) % 1;
    const a = angle + (index / drops) * Math.PI * 2;
    const from = WAFER_R + phase * reach * (stopR - WAFER_R);
    return { a, from, to: Math.min(from + length, stopR) };
  });
}

export function splashSvg(angle, level, stopR, now) {
  return splashDrops(angle, level, stopR, now)
    .map(({ a, from, to }) => `<path d="${k.pathOf([at([...polar(0, 0, from, a), SPLASH.z]), at([...polar(0, 0, to, a), SPLASH.z])])}"/>`)
    .join("");
}

function targetList() {
  const list = [];
  const add = (key, type, actions, extra = {}) => list.push({ key, type, unit: UNITS[type], actions, ...extra });
  const switchAction = { gripper: ["Open", "Close"], ringLift: ["Top", "Down"], shutter: ["Open", "Close"], vent: ["Open", "Close"], damper: ["AEX", "SEX"] };
  const holds = (type) => [...switchAction[type], "Hold"];
  const select = ["Select1", "Select2", "Select3"];
  add("Holder", "spin", ["CW", "CCW", "Stop", "Home", "Hold"]);
  add("Holder.Gripper", "gripper", holds("gripper"));
  add("OutsideRing", "spin", ["CW", "CCW", "Stop"]);
  add("OutsideRing.Lift", "ringLift", holds("ringLift"));
  add("Shutter", "shutter", holds("shutter"));
  EXHAUST_ANGLE_DEG.forEach((_, index) => add(`SideVent${index + 1}`, "vent", holds("vent"), { index }));
  add("Damper", "damper", holds("damper"));
  CUP_OUTER_R.forEach((_, index) => {
    ["Master", "Slave"].forEach((side) => add(`Cup${index + 1}.${side}`, "cup", ["Top", "Middle", "Down", "Hold", ...JOG_ACTIONS], { index, side }));
    add(`Cup${index + 1}.Drain`, "drain", select, { index });
  });
  CORNERS.forEach((_, index) => {
    add(`Arm${index + 1}.Lift`, "lift", ["Top", "Down", "Hold"], { index });
    add(`Arm${index + 1}.Rotate`, "rotate", [...Object.keys(ARM_ROTATE_DEG), "Hold", ...JOG_ACTIONS], { index });
    for (let nozzle = 0; nozzle < NOZZLE_COUNT[index]; nozzle++) add(`Arm${index + 1}.Nozzle${nozzle + 1}`, "nozzle", ["On", "Off"], { index });
    for (let port = 0; port < PORT_COUNT[index]; port++) add(`Arm${index + 1}.Port${port + 1}`, "port", ["On", "Off"], { index });
    if (PORT_COUNT[index]) add(`Arm${index + 1}.PortDrain`, "drain", select, { index });
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
    ...(PORT_COUNT[index] ? [single(`Arm${index + 1}.PortDrain`, "PD")] : []),
  ];
  const sideAlign = ["start", "end", "end", "start"];
  const fixedSpot = FIXED_ANGLE_DEG.map((degrees) => [...polar(0, 0, FACE_R + 8, degrees * RAD), FIXED_WALL.z]);
  const exhaustSpot = EXHAUST_ANGLE_DEG.map((_, index) => onFace(index, FACE_R + 6, 0, VENT_Z - VENT.h / 2 - 16));
  const backCorner = { 2: [-FACE_R, FACE_R], 3: [FACE_R, -FACE_R] };
  const exhaustBlock = (spot, index) => {
    const edge = backCorner[index];
    const lines = [single(`SideVent${index + 1}`, `Vent${index + 1}`), single(`ExhaustFan${index + 1}`, `Fan${index + 1}`)];
    if (!edge) return block(`Exhaust${index + 1}`, spot, sideAlign[index], lines);
    const outside = at([...edge, VENT_Z])[0] + (index === 2 ? -10 : 10);
    return { ...block(`Exhaust${index + 1}`, [...edge, VENT_Z], sideAlign[index], lines), anchor: [outside, at([...edge, VENT_Z])[1]] };
  };
  return [
    block("Holder", [-10, 100, 0], "end", [single("Holder", "Holder"), single("Holder.Gripper", "Grip")]),
    block("OutsideRing", [10, 100, 0], "start", [single("OutsideRing", "Ring"), single("OutsideRing.Lift", "Lift")]),
    block("Cups", [92, -64, 0], "start", CUP_OUTER_R.flatMap((_, i) => [{ keys: [`Cup${i + 1}.Master`, `Cup${i + 1}.Slave`], prefix: `Cup${i + 1}` }, single(`Cup${i + 1}.Drain`, `D${i + 1}`)])),
    ...CORNERS.map((_, i) => block(`Arm${i + 1}`, armPost(i), armAlign[i], armLines(i), [armAlign[i] === "end" ? -8 : 8, 0])),
    ...fixedSpot.map((spot, i) => block(`FixedNozzle${i + 1}`, spot, sideAlign[i], [single(`FixedNozzle${i + 1}`, `FN${i + 1}`)])),
    block("Shutter", [GATE.x + GATE.w + 8, GATE.y, GATE_Z + GATE_H + 12], "start", [single("Shutter", "Door")]),
    block("Damper", [0, 0, DAMPER_Z - DAMPER.pipeR - 46], "middle", [single("Damper", "Damper")]),
    ...exhaustSpot.map(exhaustBlock),
  ];
}
