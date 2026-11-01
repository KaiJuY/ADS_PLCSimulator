import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import * as k from "./kit/iso-kit.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const flag = (name) => process.argv.find((arg) => arg.startsWith(`--${name}=`))?.slice(name.length + 3);
const theme = process.argv.includes("--dark") ? "dark" : "light";
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

const HOLDER_R = 40;
const HOLDER_H = 14;
const WAFER_R = 34;
const WAFER_T = 2;
const WAFER_TOP = HOLDER_H + WAFER_T;
const SPIN_TICK_ANGLE = 0;

const RING = { inner: 41.5, outer: 45, height: 10 };

const CUP_INNER_R = [49, 57, 65, 73];
const CUP_WALL = 3;
const CUP_STEPS = 72;
const CUP_RIM = {
  Down: [3, 3, 3, 3],
  Middle: [16, 22, 28, 34],
  Top: [30, 40, 50, 60],
};
const CUP_INITIAL = "Down";

const CORNERS = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
const POST_OFFSET = 105;
const POST_R = 4.5;
const POST_H = 98;
const BOOM_W = 7;
const BOOM_H = 7;
const COLLAR_R = 6.5;
const HEAD_W = 11;
const HEAD_DROP = 3;
const HEAD_PAD = 7;
const NOZZLE_R = 1.7;
const NOZZLE_LEN = 6;
const NOZZLE_COUNT = [3, 3, 2, 1];
const NOZZLE_PITCH_DEG = 7;
const ARM_Z = { Top: 84, Down: 66 };
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

const PORT_H = 6;
const PORT_W = 11;
const PORT_HOLE_R = 2.4;
const PORT_JET_H = 12;
const PORT_COUNT = [3, 3, 0, 0];

const JET_R = 1.1;
const FIXED_ANGLE_DEG = [0, 90, 180, 270];
const FIXED_R = 86;
const FIXED_POST_R = 2.4;
const FIXED_Z = 34;
const FIXED_SPOUT = { reach: 9, drop: 3 };
const FIXED_LAND_R = 6;

const FAN_ANGLE_DEG = [0, 90, 180, 270];
const FAN_R = 117;
const FAN_LONG = 26;
const FAN_SHORT = 22;
const FAN_H = 14;
const FAN_HOLE_R = 3.3;

const SHELL_H = 118;
const FFU_T = 5;
const FFU_GRID = 26;
const FFU_FAN_R = 38;
const DOOR = { x0: -46, x1: 46, z0: 6, z1: 86 };
const SHELL_FACE_OPACITY = 0.07;
const FFU_OPACITY = 0.4;

const COLORS = {
  plate: "#62768a",
  holder: "#738b66",
  ring: "#b9c4cf",
  wafer: "#1f9fe0",
  arm: "#5aa22e",
  cup: ["#c3ccd6", "#aab5c1", "#919eac", "#78879a"],
  port: "#4d5560",
  fixed: "#3f6f78",
  fanOff: "#7e2a1e",
  fanOn: "#f0452a",
  ffu: "#9aa5b1",
  door: "#7d93a8",
};

const shadeOf = (hex, factor) => {
  const value = parseInt(hex.slice(1), 16);
  const part = (shift) => Math.max(0, Math.min(255, Math.round(((value >> shift) & 255) * factor)));
  return `#${[16, 8, 0].map((shift) => part(shift).toString(16).padStart(2, "0")).join("")}`;
};
const tintRule = (name, hex) => `.t-${name}{--anatomy-paper:${hex};--anatomy-top:${shadeOf(hex, 1.22)};--anatomy-shade-0:${shadeOf(hex, 0.62)};--anatomy-shade-1:${shadeOf(hex, 0.76)};--anatomy-shade-2:${shadeOf(hex, 0.9)};--anatomy-shade-3:${shadeOf(hex, 1.04)};--anatomy-hi:${shadeOf(hex, 0.42)};--anatomy-mid:${shadeOf(hex, 0.55)};--anatomy-lo:${shadeOf(hex, 0.5)};--anatomy-faint:${shadeOf(hex, 0.58)};}`;
const TINT_CSS = [
  ...Object.entries(COLORS).filter(([, hex]) => typeof hex === "string").map(([name, hex]) => tintRule(name, hex)),
  ...COLORS.cup.map((hex, index) => tintRule(`cup${index + 1}`, hex)),
  ".t-liquid{--anatomy-paper:rgba(60,170,255,.6);--anatomy-top:rgba(130,205,255,.7);--anatomy-shade-0:rgba(40,140,235,.65);--anatomy-shade-1:rgba(40,140,235,.65);--anatomy-shade-2:rgba(60,170,255,.6);--anatomy-shade-3:rgba(60,170,255,.6);--anatomy-hi:#2b8fd8;--anatomy-faint:#2b8fd8;}",
  ".jet-line{fill:none;stroke:rgba(60,170,255,.6);stroke-width:3;stroke-linecap:round}",
  ".spout-line{fill:none;stroke:#3f6f78;stroke-width:3;stroke-linecap:round}",
  ".shell-face{fill:#8aa;stroke:none}",
  ".shell-edge{fill:none;stroke:var(--anatomy-mid);stroke-width:.8}",
  ".door-panel{fill:#7d93a8;fill-opacity:.35;stroke:var(--anatomy-mid);stroke-width:.9}",
  ".part-note{position:absolute;left:0;right:0;bottom:0}",
].join("");

const P = k.fitProjection(
  [...k.boxCorners(PLATE, -PLATE_H, 0), ...k.boxCorners(PLATE, 0, SHELL_H + FFU_T)],
  WIDTH,
  HEIGHT,
  VIEW,
);

const at = (point) => k.iso(point, P);
const S = (paths, style) => k.solidSvg(paths, style);
const L = (d, style) => k.lineSvg(d, style);
const depth = ([x, y, z = 0]) => k.depthOf([x, y, z], P);
const tinted = (name, content, id, extra = "") => `<g${id ? ` id="${id}"` : ""} class="t-${name}"${extra}>${content}</g>`;
const shownIf = (on) => (on ? "" : ' style="display:none"');

const polar = (cx, cy, radius, angle) => [cx + radius * Math.cos(angle), cy + radius * Math.sin(angle)];
const rectRing = ([ax, ay], [bx, by], width) => {
  const length = Math.hypot(bx - ax, by - ay) || 1;
  const nx = (-(by - ay) / length) * (width / 2);
  const ny = ((bx - ax) / length) * (width / 2);
  return [[ax + nx, ay + ny], [bx + nx, by + ny], [bx - nx, by - ny], [ax - nx, ay - ny]];
};
const solidRing = (ring, z, height, style) => S(k.extrude(ring, z, height, P, { convex: true }), style);

const viewerAngle = VIEW.azimuth * RAD;
const facesViewer = (angle) => Math.cos(angle - viewerAngle) > 0;
const lightOf = (normal) => {
  const [lx, ly] = [-Math.sin(viewerAngle), Math.cos(viewerAngle)];
  return Math.min(k.SHADES - 1, Math.max(0, Math.floor(((normal[0] * lx + normal[1] * ly + 1) / 2) * k.SHADES)));
};

function ringHalves(inner, outer, zOf, steps = CUP_STEPS) {
  const step = (Math.PI * 2) / steps;
  const angles = Array.from({ length: steps }, (_, index) => index * step);
  const rimAt = (radius, angle) => at([radius * Math.cos(angle), radius * Math.sin(angle), zOf(radius * Math.cos(angle))]);
  const floorAt = (radius, angle) => at([radius * Math.cos(angle), radius * Math.sin(angle), 0]);
  const quad = (radius, angle) => k.pathOf([rimAt(radius, angle), rimAt(radius, angle + step), floorAt(radius, angle + step), floorAt(radius, angle)], true);
  const arc = (radius, list, bottom) => k.pathOf(list.flatMap((angle) => [bottom ? floorAt(radius, angle) : rimAt(radius, angle), bottom ? floorAt(radius, angle + step) : rimAt(radius, angle + step)]));
  const half = (front) => {
    const list = angles.filter((angle) => facesViewer(angle + step / 2) === front);
    const radius = front ? outer : inner;
    const shades = Array.from({ length: k.SHADES }, () => []);
    const wall = list.map((angle) => {
      const mid = angle + step / 2;
      const normal = front ? [Math.cos(mid), Math.sin(mid)] : [-Math.cos(mid), -Math.sin(mid)];
      shades[lightOf(normal)].push(quad(radius, angle));
      return quad(radius, angle);
    });
    const rim = list.map((angle) => k.pathOf([rimAt(outer, angle), rimAt(outer, angle + step), rimAt(inner, angle + step), rimAt(inner, angle)], true));
    const bodySvg = `<path class="iso-fill" d="${wall.join("")}"/>`
      + shades.map((paths, shade) => (paths.length ? `<path class="iso-shade" data-shade="${shade}" d="${paths.join("")}"/>` : "")).join("")
      + `<path class="iso-top" d="${rim.join("")}"/>`;
    const edges = L(arc(radius, list, true), { tone: "hi" })
      + L(arc(radius, list, false), { tone: "hi" })
      + L(arc(front ? inner : outer, list, false), { tone: "lo" })
      + L(arc(front ? outer : inner, list, false), { tone: "hi" });
    return `<g class="iso-solid">${bodySvg}${edges}</g>`;
  };
  return { back: half(false), front: half(true) };
}

const flatHeight = (value) => () => value;
const slantHeight = (west, east, reach) => (x) => west + ((east - west) * (x + reach)) / (2 * reach);

function cupParts(index, west, east) {
  const inner = CUP_INNER_R[index];
  const outer = inner + CUP_WALL;
  const halves = ringHalves(inner, outer, slantHeight(west, east, outer));
  const name = `t-cup${index + 1}`;
  return {
    back: `<g id="Cup${index + 1}-back" class="${name}">${halves.back}</g>`,
    front: `<g id="Cup${index + 1}-front" class="${name}">${halves.front}</g>`,
  };
}

function outsideRingParts(angle) {
  const halves = ringHalves(RING.inner, RING.outer, flatHeight(RING.height), 48);
  const ticks = k.radialTicks(0, 0, RING.outer - 0.6, 24, 6, RING.height, [1.4, 2.8], P, angle);
  const front = halves.front + L(ticks.minor, { tone: "lo" }) + L(ticks.major, { tone: "mid" });
  return { back: tinted("ring", halves.back, "OutsideRing-back"), front: tinted("ring", front, "OutsideRing-front") };
}

function holderSvg(angle) {
  const notchFrom = polar(0, 0, WAFER_R - 6, angle);
  const notchTo = polar(0, 0, WAFER_R - 0.6, angle);
  const holder = S(k.cylinder(0, 0, HOLDER_R, 0, HOLDER_H, P, 72, 0.6), { tone: "hi" })
    + L(k.sideArc(0, 0, HOLDER_R, 5, P, 40), { tone: "faint" });
  const wafer = S(k.cylinder(0, 0, WAFER_R, HOLDER_H, WAFER_T, P, 72), { tone: "hi" })
    + L(k.ring(0, 0, WAFER_R - 2.5, WAFER_TOP, P, 72), { tone: "faint" })
    + L(k.lineOnTop(notchFrom, notchTo, WAFER_TOP, P), { tone: "hi", free: "start" });
  return tinted("holder", holder) + tinted("wafer", wafer, "Holder");
}

function plateSvg() {
  const halo = `<path class="iso-halo" d="${k.haloOf(PLATE, -PLATE_H, PLATE_H, P)}"/>`;
  const slab = S(k.slabOf(PLATE, -PLATE_H, PLATE_H, P, 8, 1.6), { tone: "hi" })
    + L(k.planOutline(k.insetPlan(PLATE, 6), 0, P), { tone: "faint" });
  return halo + tinted("plate", slab, "Platform");
}

const armGeometry = (index) => {
  const [sx, sy] = CORNERS[index];
  const post = [sx * POST_OFFSET, sy * POST_OFFSET];
  return { post, length: Math.hypot(post[0], post[1]), toCenter: Math.atan2(-post[1], -post[0]) };
};

const nozzlePoints = (index, rotateDeg) => {
  const { post, length, toCenter } = armGeometry(index);
  const boom = toCenter + rotateDeg * RAD;
  return Array.from({ length: NOZZLE_COUNT[index] }, (_, nozzle) => polar(post[0], post[1], length, boom + nozzle * NOZZLE_PITCH_DEG * RAD));
};

const tangentOf = (index, rotateDeg) => {
  const { toCenter } = armGeometry(index);
  const boom = toCenter + rotateDeg * RAD;
  return [-Math.sin(boom), Math.cos(boom)];
};

const spanOf = (points, tangent, pad) => [
  [points[0][0] - tangent[0] * pad, points[0][1] - tangent[1] * pad],
  [points[points.length - 1][0] + tangent[0] * pad, points[points.length - 1][1] + tangent[1] * pad],
];

function armBodySvg(index, rotateDeg, liftZ) {
  const { post } = armGeometry(index);
  const points = nozzlePoints(index, rotateDeg);
  const [from, to] = spanOf(points, tangentOf(index, rotateDeg), HEAD_PAD);
  const out = [solidRing(rectRing(post, points[0], BOOM_W), liftZ, BOOM_H, { tone: "hi" })];
  out.push(S(k.cylinder(post[0], post[1], COLLAR_R, liftZ - 1, BOOM_H + 2, P, 24), { tone: "hi" }));
  const headZ = liftZ - HEAD_DROP;
  for (const [x, y] of [...points].sort((a, b) => depth([a[0], a[1]]) - depth([b[0], b[1]]))) {
    out.push(S(k.cylinder(x, y, NOZZLE_R, headZ - NOZZLE_LEN, NOZZLE_LEN, P, 12), { tone: "hi" }));
  }
  out.push(solidRing(rectRing(from, to, HEAD_W), headZ, BOOM_H + HEAD_DROP, { tone: "hi" }));
  return out.join("");
}

const jetColumn = ([x, y], topZ, floorZ) => S(k.cylinder(x, y, JET_R, floorZ, topZ - floorZ, P, 10), { tone: "faint", crease: "none" });

function armJetsSvg(index, rotateDeg, liftZ, on) {
  const tipZ = liftZ - HEAD_DROP - NOZZLE_LEN;
  return nozzlePoints(index, rotateDeg).map((point, nozzle) => {
    const floor = Math.hypot(point[0], point[1]) <= WAFER_R ? WAFER_TOP : 0;
    return tinted("liquid", jetColumn(point, tipZ, floor), `Arm${index + 1}-Nozzle${nozzle + 1}-jet`, shownIf(on));
  }).join("");
}

function armSvg(index, rotateName, liftName, on) {
  const rotateDeg = ARM_ROTATE_DEG[rotateName];
  const liftZ = ARM_Z[liftName];
  const { post } = armGeometry(index);
  const postSvg = S(k.cylinder(post[0], post[1], POST_R, 0, POST_H, P, 24, 0.5), { tone: "hi" });
  return [
    tinted("arm", postSvg, `Arm${index + 1}-post`),
    armJetsSvg(index, rotateDeg, liftZ, on),
    tinted("arm", armBodySvg(index, rotateDeg, liftZ), `Arm${index + 1}`),
  ].join("");
}

function portSvg(index, on) {
  const points = nozzlePoints(index, ARM_ROTATE_DEG.NozzlePort);
  const count = PORT_COUNT[index];
  if (!count) return "";
  const [from, to] = spanOf(points.slice(0, count), tangentOf(index, ARM_ROTATE_DEG.NozzlePort), HEAD_PAD);
  const base = solidRing(rectRing(from, to, PORT_W), 0, PORT_H, { tone: "hi" })
    + L(points.slice(0, count).map(([x, y]) => k.ring(x, y, PORT_HOLE_R, PORT_H, P, 14)).join(""), { tone: "hi" });
  const jets = points.slice(0, count).map((point, nozzle) => tinted("liquid", jetColumn(point, PORT_H + PORT_JET_H, PORT_H), `Arm${index + 1}-Port${nozzle + 1}-jet`, shownIf(on))).join("");
  return tinted("port", base, `Arm${index + 1}-PortBase`) + jets;
}

function fixedNozzleSvg(index, on) {
  const angle = FIXED_ANGLE_DEG[index] * RAD;
  const base = polar(0, 0, FIXED_R, angle);
  const tip = polar(0, 0, FIXED_R - FIXED_SPOUT.reach, angle);
  const tipZ = FIXED_Z - FIXED_SPOUT.drop;
  const land = polar(0, 0, FIXED_LAND_R, angle);
  const stand = S(k.cylinder(base[0], base[1], FIXED_POST_R, 0, FIXED_Z, P, 14), { tone: "hi" })
    + `<path class="spout-line" d="${k.pathOf([at([base[0], base[1], FIXED_Z]), at([tip[0], tip[1], tipZ])])}"/>`;
  const jet = `<path class="jet-line" d="${k.pathOf([at([tip[0], tip[1], tipZ]), at([land[0], land[1], WAFER_TOP])])}"/>`;
  return tinted("fixed", stand, `FixedNozzle${index + 1}-base`) + `<g id="FixedNozzle${index + 1}-jet"${shownIf(on)}>${jet}</g>`;
}

function fanSvg(index, on) {
  const angle = FAN_ANGLE_DEG[index] * RAD;
  const [cx, cy] = polar(0, 0, FAN_R, angle);
  const alongX = index % 2 === 0;
  const plan = alongX
    ? { x: cx - FAN_SHORT / 2, y: cy - FAN_LONG / 2, w: FAN_SHORT, d: FAN_LONG, r: 2 }
    : { x: cx - FAN_LONG / 2, y: cy - FAN_SHORT / 2, w: FAN_LONG, d: FAN_SHORT, r: 2 };
  const holes = [[-1, -1], [1, -1], [-1, 1], [1, 1]].map(([a, b]) => (alongX ? [cx + a * 4.5, cy + b * 6] : [cx + b * 6, cy + a * 4.5]));
  const variant = (name, lit) => {
    const block = S(k.slabOf(plan, 0, FAN_H, P, 4, 0.6), { tone: "hi", lit })
      + L(holes.map(([x, y]) => k.ring(x, y, FAN_HOLE_R, FAN_H, P, 14)).join(""), { tone: "hi" });
    return tinted(name, block);
  };
  return `<g id="ExhaustFan${index + 1}">`
    + `<g class="fan-off"${shownIf(!on)}>${variant("fanOff", false)}</g>`
    + `<g class="fan-on"${shownIf(on)}>${variant("fanOn", true)}</g>`
    + "</g>";
}

const wallCorner = (x, y, z) => at([x, y, z]);
const rectAt = (x0, y0, x1, y1, z) => [[x0, y0], [x1, y0], [x1, y1], [x0, y1]].map(([x, y]) => at([x, y, z]));

function shellSvg() {
  const x0 = PLATE.x;
  const x1 = PLATE.x + PLATE.w;
  const y0 = PLATE.y;
  const y1 = PLATE.y + PLATE.d;
  const south = [wallCorner(x0, y1, 0), wallCorner(x1, y1, 0), wallCorner(x1, y1, SHELL_H), wallCorner(x0, y1, SHELL_H)];
  const east = [wallCorner(x1, y0, 0), wallCorner(x1, y1, 0), wallCorner(x1, y1, SHELL_H), wallCorner(x1, y0, SHELL_H)];
  const faces = [south, east].map((points) => `<path class="shell-face" fill-opacity="${SHELL_FACE_OPACITY}" d="${k.pathOf(points, true)}"/>`).join("");
  const posts = [[x0, y0], [x1, y0], [x1, y1], [x0, y1]].map(([x, y]) => k.pathOf([wallCorner(x, y, 0), wallCorner(x, y, SHELL_H)])).join("");
  const top = k.pathOf(rectAt(x0, y0, x1, y1, SHELL_H), true);
  const door = [wallCorner(DOOR.x0, y1, DOOR.z0), wallCorner(DOOR.x1, y1, DOOR.z0), wallCorner(DOOR.x1, y1, DOOR.z1), wallCorner(DOOR.x0, y1, DOOR.z1)];
  const handle = k.pathOf([wallCorner(DOOR.x1 - 8, y1, DOOR.z0 + 30), wallCorner(DOOR.x1 - 8, y1, DOOR.z0 + 46)]);
  const seam = k.pathOf([wallCorner(0, y1, DOOR.z0), wallCorner(0, y1, DOOR.z1)]);
  const frame = `<path class="shell-edge" d="${posts}${top}"/>`;
  return `<g id="Shell">${faces}${frame}</g>`
    + `<g id="Door"><path class="door-panel" d="${k.pathOf(door, true)}"/><path class="shell-edge" d="${handle}${seam}"/></g>`;
}

function ffuSvg() {
  const plan = { x: PLATE.x, y: PLATE.y, w: PLATE.w, d: PLATE.d, r: 4 };
  const slab = S(k.slabOf(plan, SHELL_H, FFU_T, P, 6), { tone: "hi" });
  const top = SHELL_H + FFU_T;
  const lines = [];
  for (let offset = PLATE.x + FFU_GRID; offset < PLATE.x + PLATE.w; offset += FFU_GRID) {
    lines.push(k.lineOnTop([offset, PLATE.y + 6], [offset, PLATE.y + PLATE.d - 6], top, P));
    lines.push(k.lineOnTop([PLATE.x + 6, offset], [PLATE.x + PLATE.w - 6, offset], top, P));
  }
  const fan = k.ring(0, 0, FFU_FAN_R, top, P, 48)
    + [0, 1, 2, 3, 4, 5].map((blade) => k.lineOnTop([0, 0], polar(0, 0, FFU_FAN_R - 3, (blade * Math.PI) / 3 + 0.4), top, P)).join("");
  return `<g id="FFU" style="opacity:${FFU_OPACITY}">${tinted("ffu", slab)}${L(lines.join(""), { tone: "lo" })}${L(fan, { tone: "mid" })}</g>`;
}

const armsByDepth = [0, 1, 2, 3].sort((a, b) => depth([...armGeometry(a).post, 0]) - depth([...armGeometry(b).post, 0]));

function buildPose() {
  const rotate = poseNames.find((name) => name in ARM_ROTATE_DEG) ?? ARM_ROTATE_INITIAL;
  const named = (name) => poseNames.includes(name);
  const cupName = ["Down", "Middle", "Top"].find(named) ?? CUP_INITIAL;
  const armLift = ["Down", "Top"].find(named) ?? ARM_LIFT_INITIAL;
  const sketch = named("sketch");
  const cupNames = sketch ? ["Down", "Down", "Top", "Top"] : Array(4).fill(cupName);
  return { rotate, armLift, cupNames };
}

function build() {
  const pose = buildPose();
  const cups = pose.cupNames.map((name, index) => cupParts(index, CUP_RIM[name][index], CUP_RIM[name][index]));
  const ringParts = outsideRingParts(SPIN_TICK_ANGLE);
  const arms = armsByDepth.map((index) => {
    const needs = ARM_ROTATE_NEEDS[pose.rotate] ?? 0;
    const rotateName = needs > NOZZLE_COUNT[index] ? ARM_ROTATE_INITIAL : pose.rotate;
    return armSvg(index, rotateName, pose.armLift, showActive);
  });
  const body = [
    plateSvg(),
    ...[0, 1, 2, 3].map((index) => fanSvg(index, showActive)),
    ...[0, 1, 2, 3].map((index) => portSvg(index, showActive)),
    ...[0, 1, 2, 3].map((index) => fixedNozzleSvg(index, showActive)),
    ...[3, 2, 1, 0].map((index) => cups[index].back),
    ringParts.back,
    holderSvg(SPIN_TICK_ANGLE),
    ringParts.front,
    ...[0, 1, 2, 3].map((index) => cups[index].front),
    ...arms,
    shellSvg(),
    ffuSvg(),
  ];
  return k.figureSvg({
    width: WIDTH,
    height: HEIGHT,
    label: "Polar2 chamber: a wafer holder at the centre of a platform, four nested cups around it, four arms with nozzle heads on the corner posts, fixed nozzles, exhaust fans and a closed door.",
    body,
  });
}

const runtime = `
const P = ${JSON.stringify(P)};
`;

const page = k.plateHtml({
  fig: "Polar2",
  title: "Polar2 chamber",
  hint: "Static drawing",
  readout: "",
  keys: [
    { mark: "lit", label: "Holder and wafer" },
    { mark: "green", label: "Arm1 to Arm4, one per corner" },
    { mark: "dim", label: "Cup1 (inner) to Cup4 (outer)" },
  ],
  caption: "Initial pose: Cups Down, Arms at NozzlePort and Top, Holder and OutsideRing stopped, no liquid, exhaust fans off.",
  body: `<style>${TINT_CSS}</style><div class="iso-stage">${build()}</div>`,
});

writeFileSync(outFile, k.pageHtml({ title: "Polar2 Simulation", theme, body: page, script: kitScript() + runtime, width: 760 }));
console.log("wrote", outFile);
