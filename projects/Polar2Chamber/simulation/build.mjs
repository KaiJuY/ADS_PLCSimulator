import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import * as k from "./kit/iso-kit.mjs";
import * as g from "./parts.mjs";

const {
  P, WIDTH, HEIGHT, PLATE, PLATE_H, PLATE_OPACITY, WALL, SHELL_H, WALL_OPACITY, SLOT, GATE, GATE_Z, GATE_H,
  HOUSING_R, HOUSING_H, CHUCK_R, CHUCK_TOP, CUP_RIM, CUP_INITIAL, PIVOT_R, PIVOT_H, NOZZLE_COUNT, ARM_Z,
  ARM_LIFT_INITIAL, ARM_ROTATE_INITIAL, ARM_ROTATE_DEG, ARM_ROTATE_NEEDS, PORT_COUNT, PORT_FLOOR, PORT_JET_H,
  GRIPPER_INITIAL, SHUTTER_Z, SHUTTER_INITIAL, DAMPER_INITIAL, TARGETS,
  at, S, L, D, depth, armGeometry, cupParts, outsideRingParts, ringTicksSvg, holderSvg, portCentre, nozzlePoints, jetColumn,
  armBodySvg, armJetSvg, fixedNozzleParts, baseSvg, ventSvg, fanSvg, damperSvg, shutterSvg, portCupSvg, exhaustFlowSvg,
} = g;

const HERE = dirname(fileURLToPath(import.meta.url));
const flag = (name) => process.argv.find((arg) => arg.startsWith(`--${name}=`))?.slice(name.length + 3);
const theme = process.argv.includes("--light") ? "light" : "dark";
const showActive = process.argv.includes("--on");
const poseNames = (flag("pose") ?? "").split(",").filter(Boolean);
const outFile = flag("out") ? resolve(flag("out")) : join(HERE, "index.html");
if (poseNames.length && !flag("out")) throw new Error("--pose needs --out=<file> so index.html stays the initial pose");

const read = (...parts) => readFileSync(join(HERE, ...parts), "utf8").replace(/\r\n/g, "\n");
const inlined = (source) => source
  .replace(/^import .*$/gm, "")
  .replace(/^export /gm, "")
  .replace(/<\/(body|html)/gi, "<\\/$1");
const script = [
  inlined(read("kit", "iso-kit.mjs")),
  `const k = { ${Object.keys(k).join(", ")} };`,
  inlined(read("parts.mjs")),
  read("runtime.js"),
].join("\n");

const shownIf = (show) => (show ? "" : ' style="display:none"');
const group = (id, content, extra = "") => `<g id="${id}"${extra}>${content}</g>`;

function plateSvg() {
  const halo = `<path class="iso-halo" d="${k.haloOf(PLATE, -PLATE_H, PLATE_H, P)}"/>`;
  const slab = S(k.slabOf(PLATE, -PLATE_H, PLATE_H, P, 8, 1.6), { tone: "mid" })
    + L(k.planOutline(k.insetPlan(PLATE, 5), 0, P), { tone: "lo" });
  const screws = k.corners(PLATE, 10);
  const dots = D(screws.map(([x, y]) => at([x, y, 0])), { size: 0.5 });
  return group("Platform", `${halo}${slab}${L(screws.map(([x, y]) => k.ring(x, y, 2.2, 0, P, 16)).join(""), { tone: "lo" })}${dots}`, ` style="opacity:${PLATE_OPACITY}"`);
}

const wallPlans = {
  back: { x: PLATE.x, y: PLATE.y, w: PLATE.w, d: WALL, r: 1 },
  left: { x: PLATE.x, y: PLATE.y + WALL, w: WALL, d: PLATE.d - 2 * WALL, r: 0.5 },
  front: { x: PLATE.x, y: PLATE.y + PLATE.d - WALL, w: PLATE.w, d: WALL, r: 1 },
  right: { x: PLATE.x + PLATE.w - WALL, y: PLATE.y + WALL, w: WALL, d: PLATE.d - 2 * WALL, r: 0.5 },
};

const wallSvg = (name) => S(k.slabOf(wallPlans[name], 0, SHELL_H, P, 3, 0.4), { tone: "mid", inner: L(k.sideSeam(wallPlans[name], SHELL_H - 8, P, 3), { tone: "faint" }) });
const wallsSvg = () => group("Walls", ["back", "left", "front", "right"].map(wallSvg).join(""), ` style="opacity:${WALL_OPACITY}"`);

function slotAndGateSvg() {
  const y = PLATE.y + PLATE.d + 0.05;
  const slot = [[SLOT.x0, SLOT.z0], [SLOT.x1, SLOT.z0], [SLOT.x1, SLOT.z1], [SLOT.x0, SLOT.z1]].map(([x, z]) => at([x, y, z]));
  const frame = `<path d="${k.pathOf(slot, true)}" style="fill:var(--anatomy-shade-0);stroke:var(--anatomy-lo);stroke-width:var(--anatomy-weight)"/>`;
  const rails = [GATE.x - 4, GATE.x + GATE.w + 1.6].map((x) => S(k.slabOf({ x, y: GATE.y, w: 2.4, d: GATE.d, r: 0.5 }, GATE_Z - 2, 2 * GATE_H, P, 2, 0.3), { tone: "mid", crease: "lo" })).join("");
  return `${frame}${rails}${group("Shutter", shutterSvg(SHUTTER_Z[SHUTTER_INITIAL]))}`;
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

function pivotSvg(index) {
  const [x, y] = armGeometry(index).post;
  return S(k.cylinder(x, y, PIVOT_R + 2.5, 0, 1.6, P, 28), { tone: "lo" })
    + S(k.cylinder(x, y, PIVOT_R, 1.6, PIVOT_H - 1.6, P, 28, 0.5), { tone: "mid", crease: "none" })
    + L(k.sideArc(x, y, PIVOT_R, 6, P, 20), { tone: "faint" })
    + L(k.ring(x, y, PIVOT_R - 2, PIVOT_H, P, 24), { tone: "lo" });
}

function portSvg(index) {
  const count = PORT_COUNT[index];
  const jets = nozzlePoints(index, ARM_ROTATE_DEG.NozzlePort).slice(0, count).map((point, nozzle) => group(`Arm${index + 1}-Port${nozzle + 1}-jet`, jetColumn(point, PORT_FLOOR + PORT_JET_H, PORT_FLOOR), ` class="liquid"${shownIf(showActive)}`)).join("");
  return group(`Arm${index + 1}-PortCup`, portCupSvg(index)) + jets;
}

const fanAndVent = (index) => group(`Fan${index + 1}`, fanSvg(index, 0)) + group(`SideVent${index + 1}`, ventSvg(index, 0))
  + group(`Exhaust${index + 1}-flow`, exhaustFlowSvg(index), shownIf(showActive));

const armsByDepth = [0, 1, 2, 3].sort((a, b) => depth([...armGeometry(a).post, 0]) - depth([...armGeometry(b).post, 0]));

function buildPose() {
  const named = (name) => poseNames.includes(name);
  const rotate = poseNames.find((name) => name in ARM_ROTATE_DEG) ?? ARM_ROTATE_INITIAL;
  const cupName = ["Down", "Middle", "Top"].find(named) ?? CUP_INITIAL;
  const armLift = ["Down", "Top"].find(named) ?? ARM_LIFT_INITIAL;
  const cupNames = named("sketch") ? ["Down", "Down", "Top", "Top"] : Array(4).fill(cupName);
  return { rotate, armLift, cupNames };
}

function armSvg(index, pose) {
  const needs = ARM_ROTATE_NEEDS[pose.rotate] ?? 0;
  const rotateDeg = ARM_ROTATE_DEG[needs > NOZZLE_COUNT[index] ? ARM_ROTATE_INITIAL : pose.rotate];
  const liftZ = ARM_Z[pose.armLift];
  const jets = Array.from({ length: NOZZLE_COUNT[index] }, (_, nozzle) => group(`Arm${index + 1}-Nozzle${nozzle + 1}-jet`, armJetSvg(index, nozzle, rotateDeg, liftZ), ` class="liquid"${shownIf(showActive)}`)).join("");
  return group(`Arm${index + 1}-post`, pivotSvg(index)) + jets + group(`Arm${index + 1}`, armBodySvg(index, rotateDeg, liftZ));
}

function fixedSvg(index) {
  const parts = fixedNozzleParts(index);
  return group(`FixedNozzle${index + 1}-base`, parts.base) + group(`FixedNozzle${index + 1}-jet`, parts.jet, ` class="liquid"${shownIf(showActive)}`);
}

function build() {
  const pose = buildPose();
  const cupRim = pose.cupNames.map((name, index) => CUP_RIM[name][index]);
  const cups = cupRim.map((rim, index) => cupParts(index, rim, rim));
  const ring = outsideRingParts();
  const all = [0, 1, 2, 3];
  const clamp = GRIPPER_INITIAL === "Close" ? 1 : 0;
  const body = [
    group("Base", baseSvg(), ` style="opacity:${WALL_OPACITY}"`),
    ...[2, 3].map(fanAndVent),
    plateSvg(),
    wallsSvg(),
    ...[0, 1].map(fanAndVent),
    group("Damper", damperSvg(DAMPER_INITIAL === "SEX" ? 1 : 0)),
    ...all.map(portSvg),
    motorHousingSvg(),
    ...[3, 2, 1, 0].map((index) => group(`Cup${index + 1}-back`, cups[index].back)),
    group("OutsideRing-back", ring.back),
    chuckSvg(),
    group("Holder", holderSvg(0, clamp)),
    group("Splash", "", ' class="splash"'),
    group("OutsideRing-front", ring.front + group("OutsideRing-ticks", ringTicksSvg(0))),
    ...all.map((index) => group(`Cup${index + 1}-front`, cups[index].front)),
    ...all.map(fixedSvg),
    ...armsByDepth.map((index) => armSvg(index, pose)),
    slotAndGateSvg(),
    group("Labels", ""),
  ];
  return k.figureSvg({
    width: WIDTH,
    height: HEIGHT,
    label: "Polar2 single-wafer wet-clean chamber: a spin chuck with a vacuum pad, edge clamps and a wafer, four nested splash-guard cups with conical lips, four swing dispense arms with nozzle heads and nozzle port cups, fixed nozzles on the walls, a base box with four louvered side vents, fans and a two-way exhaust damper, a wafer slot with its shutter.",
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
  ".splash path{fill:none;stroke:var(--anatomy-blue);stroke-opacity:1;stroke-width:1.8;stroke-linecap:round;vector-effect:none}",
  ".jet-line{fill:none;stroke:var(--anatomy-blue);stroke-opacity:.7;stroke-width:2.2;stroke-linecap:round;vector-effect:none}",
  ".part-label{font:8.5px var(--anatomy-mono);fill:var(--anatomy-strong);paint-order:stroke;stroke:var(--anatomy-card);stroke-width:2.5px;stroke-linejoin:round;pointer-events:none}",
  ".part-label .lbl-key{fill:var(--anatomy-muted)}",
  ".sim-errors{margin:10px 0 0;padding:0;font:11px/1.5 var(--anatomy-mono);color:var(--anatomy-red);white-space:pre-wrap}",
  ".sim-errors:empty{display:none}",
].join("");

const page = k.plateHtml({
  fig: "Polar2",
  title: "Polar2 chamber",
  hint: "連線中…",
  readout: "",
  keys: [
    { mark: "lit", label: "Wafer on the spin chuck" },
    { mark: "raised", label: "Cup1 (inner) to Cup4 (outer): wall and conical lip" },
    { mark: "blue", label: "Liquid, exhaust flow and spin-off splash, shown only while active" },
  ],
  caption: "The page only displays: every motion is a Polar2 service sending sim commands over /live (Holder and Gripper, OutsideRing and Lift, Cup Master/Slave and Drain, Arm Lift/Rotate/Nozzle/Port, FixedNozzle, Shutter, SideVent, ExhaustFan, Damper).",
  body: `<style>${PAGE_EXTRA_CSS}</style><div class="iso-stage">${build()}</div><pre class="sim-errors" id="errors"></pre>`,
});

function simMismatches() {
  const path = process.env.POLAR2_SIM_CONFIG || join(HERE, "..", "config", "service-behaviors.json");
  const text = readFileSync(path, "utf8");
  const services = JSON.parse(text.startsWith(String.fromCharCode(0xfeff)) ? text.slice(1) : text).services ?? {};
  const known = new Map(TARGETS.map((entry) => [entry.key.toLowerCase(), new Set(entry.actions.map((action) => action.toLowerCase()))]));
  const found = [];
  for (const [service, definition] of Object.entries(services)) {
    for (const rule of definition.sim ?? []) {
      for (const action of [rule.action, rule.dryRunAction].filter(Boolean)) {
        if (!known.get(String(rule.target).toLowerCase())?.has(String(action).toLowerCase())) found.push(`sim mismatch: ${service} ${rule.target} ${action}`);
      }
    }
  }
  return found;
}

const mismatches = simMismatches();
if (mismatches.length) {
  console.error(mismatches.join("\n"));
  process.exitCode = 1;
} else {
  const html = k.pageHtml({ title: "Polar2 Simulation", theme, body: page, script, width: 760 });
  // A classic script keeps the top-level functions and let bindings reachable from the page self-test.
  writeFileSync(outFile, html.replace('</main><script type="module">', "</main><script>"));
  console.log("wrote", outFile);
}
