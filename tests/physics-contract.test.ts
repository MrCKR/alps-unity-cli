import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, readCsSources, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const PHYSICS_COMMANDS = [
  "physics.assign-material",
  "physics.collision-matrix",
  "physics.force",
  "physics.joint",
  "physics.linecast",
  "physics.material",
  "physics.overlap",
  "physics.raycast",
  "physics.raycast-all",
  "physics.rigidbody",
  "physics.settings",
  "physics.shapecast",
  "physics.simulation",
  "physics.validate"
];

test("physics group exposes the 14 tools via contract discovery", () => {
  assert.deepEqual(contractToolNames("physics"), [...PHYSICS_COMMANDS].sort());
});

test("physics live descriptors expose representative semantics", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "physics.raycast", summary: "Raycast in the physics scene.", description: "Raycast in the physics scene.", group: "physics", examples: [{ argv: ["tools", "run", "physics.raycast", "--input", "{}"], description: "" }] }),
    makeLiveTool({ name: "physics.raycast-all", summary: "Raycast all hits.", description: "Raycast all hits.", group: "physics", examples: [{ argv: ["tools", "run", "physics.raycast-all", "--input", "{}"], description: "" }] }),
    makeLiveTool({ name: "physics.linecast", summary: "Linecast in the physics scene.", description: "Linecast in the physics scene.", group: "physics", examples: [{ argv: ["tools", "run", "physics.linecast", "--input", "{}"], description: "" }] }),
    makeLiveTool({ name: "physics.shapecast", summary: "Shapecast in the physics scene.", description: "Supports CircleCast/BoxCast/CapsuleCast.", group: "physics", examples: [{ argv: ["tools", "run", "physics.shapecast", "--input", "{}"], description: "" }] }),
    makeLiveTool({ name: "physics.overlap", summary: "Overlap query.", description: "Overlap query supporting mode 2d and 3d.", group: "physics", examples: [{ argv: ["tools", "run", "physics.overlap", "--input", "{}"], description: "" }] }),
    makeLiveTool({ name: "physics.validate", summary: "Validate physics state.", description: "Validate physics state.", group: "physics", examples: [{ argv: ["tools", "run", "physics.validate", "--input", "{}"], description: "" }] }),
    makeLiveTool({ name: "physics.force", summary: "Apply runtime force.", description: "Apply runtime force.", group: "physics", playModeSupport: "runtime-only", sideEffects: ["runtime-state"] }),
    makeLiveTool({ name: "physics.simulation", summary: "Step runtime simulation.", description: "Step runtime simulation.", group: "physics", playModeSupport: "runtime-only" }),
    makeLiveTool({ name: "physics.settings", summary: "Mutate physics settings.", description: "Mutate physics settings.", group: "physics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "physics.collision-matrix", summary: "Read or update collision matrix.", description: "Read or update the full 32-layer collision matrix.", group: "physics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"], inputSchema: { type: "object", properties: {} } }),
    makeLiveTool({ name: "physics.material", summary: "Mutate physics materials.", description: "Mutate physics materials.", group: "physics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] }),
    makeLiveTool({ name: "physics.assign-material", summary: "Assign a physics material.", description: "Assign a physics material.", group: "physics", supportsDryRun: true, playModeSupport: "session-affecting", sideEffects: ["scene-modify"], inputSchema: { type: "object", required: ["target", "materialPath"], properties: { target: { type: "string" }, materialPath: { type: "string" } } } }),
    makeLiveTool({ name: "physics.joint", summary: "Mutate joints.", description: "Mutate joints; supports action=remove.", group: "physics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"], inputSchema: { type: "object", properties: { action: { type: "string" } } } }),
    makeLiveTool({ name: "physics.rigidbody", summary: "Read or mutate rigidbody state.", description: "Read rigidbody state without adding or modifying components.", group: "physics", playModeSupport: "session-affecting", sideEffects: ["scene-modify"] })
  );

  for (const name of ["physics.raycast", "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.overlap", "physics.validate"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.examples?.length, `${name} examples`);
  }

  const force = tools.get("physics.force")!;
  assert.equal(force.playModeSupport, "runtime-only");
  assert.ok(force.sideEffects?.includes("runtime-state"));
  assert.equal(tools.get("physics.simulation")?.playModeSupport, "runtime-only");

  for (const name of ["physics.settings", "physics.collision-matrix", "physics.material", "physics.assign-material", "physics.joint", "physics.rigidbody"]) {
    const tool = tools.get(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} session-affecting`);
    assert.ok(tool.sideEffects?.length, `${name} side effects`);
  }

  const collision = tools.get("physics.collision-matrix")!;
  assert.equal(Array.isArray(collision.inputSchema.required), false, "full matrix read must not require layerA/layerB");
  assert.match(collision.description, /full 32-layer collision matrix/i);

  const assign = tools.get("physics.assign-material")!;
  assert.equal(assign.destructiveHint, false);
  assert.equal(assign.supportsDryRun, true);
  assert.ok(assign.sideEffects?.includes("scene-modify"));
  assert.deepEqual(assign.inputSchema.required, ["target", "materialPath"]);

  const joint = tools.get("physics.joint")!;
  assert.ok((joint.inputSchema.properties as Record<string, unknown>).action);
  assert.match(joint.description, /action=remove/);

  assert.match(tools.get("physics.overlap")?.description ?? "", /mode 2d/i);
  assert.match(tools.get("physics.shapecast")?.description ?? "", /CircleCast\/BoxCast\/CapsuleCast/);
  assert.match(tools.get("physics.rigidbody")?.description ?? "", /without adding or modifying components/);
});

test("bridge specs resolve for physics tools", () => {
  for (const [cmd, expected] of [["settings", "physics.settings"], ["collision-matrix", "physics.collision-matrix"], ["material", "physics.material"], ["assign-material", "physics.assign-material"], ["joint", "physics.joint"], ["force", "physics.force"], ["overlap", "physics.overlap"], ["rigidbody", "physics.rigidbody"], ["raycast", "physics.raycast"], ["simulation", "physics.simulation"], ["raycast-all", "physics.raycast-all"], ["linecast", "physics.linecast"], ["shapecast", "physics.shapecast"], ["validate", "physics.validate"]] as const) {
    const spec = bridgeCommandRegistry.lookup("physics", cmd);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `physics ${cmd} should route via project-tool.run`);
    assert.equal(spec?.toolName, expected, `physics ${cmd} toolName should match`);
  }
});

test("contract entries exist for physics tools with present C# handler", () => {
  for (const name of PHYSICS_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("physics C# handlers are registered", async () => {
  const allSource = await readEditorCsSources();
  for (const command of PHYSICS_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("physics C# has no legacy unity_cli command form", async () => {
  const source = await readCsSources(new URL("../unitypkg/Editor/Tools/physics/", import.meta.url));
  assert.equal(source.includes("unity_cli "), false, "must not reference legacy 'unity_cli ' command form");
});

test("physics.validate C# wraps result in structured tool result", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/physics/simulation-validate.cs", import.meta.url), "utf8");
  assert.match(source, /class PhysicsValidateTool : IUnityCliTool</, "PhysicsValidateTool should exist");
  assert.match(source, /public sealed class Result/, "physics.validate should expose a structured result DTO");
  assert.match(source, /public Result Execute\(Request request, UnityCliProjectToolContext ctx\)/, "physics.validate should return structured business fields");
});

test("physics bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/physics.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "target"\)/, "bridge should forward structured target");
  assert.match(spec, /numberField\(flags, "gravityX"\)/, "bridge should forward structured gravityX");
  assert.match(spec, /boolField\(flags, "useGravity"\)/, "bridge should forward structured useGravity");
  assert.match(spec, /stringField\(flags, "shape"\)/, "bridge should forward structured shape");
  assert.match(spec, /stringField\(flags, "materialPath"\)/, "bridge should forward structured materialPath");
  assert.match(spec, /stringField\(flags, "action"\)/, "bridge should forward structured joint action");
});
