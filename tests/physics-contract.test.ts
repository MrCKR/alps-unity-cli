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
  "physics.joint.add",
  "physics.joint.remove",
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

test("physics group exposes the independent tools via contract discovery", () => {
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
    makeLiveTool({ name: "physics.joint.add", summary: "Add a joint.", description: "Add or configure a Joint component.", group: "physics", supportsDryRun: true, playModeSupport: "supported", sideEffects: ["scene-modify"], inputSchema: { type: "object", required: ["target"], properties: { target: { type: "string" } } } }),
    makeLiveTool({ name: "physics.joint.remove", summary: "Remove a joint.", description: "Remove matching Joint components.", group: "physics", supportsDryRun: true, playModeSupport: "supported", sideEffects: ["scene-modify"], inputSchema: { type: "object", required: ["target"], properties: { target: { type: "string" } } } }),
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

  for (const name of ["physics.settings", "physics.collision-matrix", "physics.material", "physics.assign-material", "physics.rigidbody"]) {
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

  for (const name of ["physics.joint.add", "physics.joint.remove"]) {
    const joint = tools.get(name)!;
    assert.equal(joint.playModeSupport, "supported");
    assert.equal(joint.supportsDryRun, true);
    assert.ok((joint.inputSchema.properties as Record<string, unknown>).target);
    assert.equal((joint.inputSchema.properties as Record<string, unknown>).action, undefined);
  }

  assert.match(tools.get("physics.overlap")?.description ?? "", /mode 2d/i);
  assert.match(tools.get("physics.shapecast")?.description ?? "", /CircleCast\/BoxCast\/CapsuleCast/);
  assert.match(tools.get("physics.rigidbody")?.description ?? "", /without adding or modifying components/);
});

test("bridge specs resolve for physics tools", () => {
  for (const [cmd, expected] of [["settings", "physics.settings"], ["collision-matrix", "physics.collision-matrix"], ["material", "physics.material"], ["assign-material", "physics.assign-material"], ["joint-add", "physics.joint.add"], ["joint-remove", "physics.joint.remove"], ["force", "physics.force"], ["overlap", "physics.overlap"], ["rigidbody", "physics.rigidbody"], ["raycast", "physics.raycast"], ["simulation", "physics.simulation"], ["raycast-all", "physics.raycast-all"], ["linecast", "physics.linecast"], ["shapecast", "physics.shapecast"], ["validate", "physics.validate"]] as const) {
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

test("physics.validate C# wraps result in structured tool result", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/physics/validate.cs", import.meta.url), "utf8");
  assert.match(source, /class PhysicsValidateTool : IUnityCliTool</, "PhysicsValidateTool should exist");
  assert.match(source, /public sealed class Result/, "physics.validate should expose a structured result DTO");
  assert.match(source, /public System\.Threading\.Tasks\.ValueTask<Result> ExecuteAsync\(Request request, UnityCliProjectToolContext ctx, System\.Threading\.CancellationToken ct = default\)/, "physics.validate should return structured business fields through the unified async signature");
});

test("physics.rigidbody mutation reports the attached component", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/physics/rigidbody.cs", import.meta.url), "utf8");
  assert.match(source, /var observedTarget = EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/, "mutation must fresh-resolve the target");
  assert.match(source, /var observedRigidbody = observedTarget == null \? null : observedTarget\.GetComponent<Rigidbody>\(\)/, "mutation must re-read the attached component");
  assert.match(source, /E_PHYSICS_RIGIDBODY_STATE_MISMATCH/, "mutation must fail when the fresh readback does not match");
  assert.match(source, /HasRigidbody = observedRigidbody != null,\s*Rigidbody = PhysicsToolSupport\.BuildRigidbodyInfo\(observedRigidbody\)/, "mutation result must report the fresh post-operation component state");
  assert.match(source, /if \(rigidbody != null && !hasSetter\)[\s\S]*PlannedEffects = new List<PhysicsPlannedEffect>\(\)[\s\S]*ctx\.CommitEffect\(\)/, "an existing Rigidbody with no setters must remain read-only");
});

test("physics.joint.add re-resolves the component after mode-safe attachment", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/physics/joint/add.cs", import.meta.url), "utf8");
  assert.match(source, /UnityCliSceneMutation\.AddComponent\(target, type\)/);
  assert.match(source, /var joint = target\.GetComponent\(type\) as Joint/);
  assert.match(source, /var observedTarget = EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/);
  assert.match(source, /var observedJoint = observedTarget == null \? null : observedTarget\.GetComponent\(type\) as Joint/);
  assert.match(source, /var observedConnectedBody = connectedBodyId == 0 \? null : EditorUtility\.InstanceIDToObject\(connectedBodyId\) as Rigidbody/);
  assert.match(source, /E_PHYSICS_JOINT_ADD_FAILED/);
  assert.match(source, /ct\.ThrowIfCancellationRequested\(\)/);
});

test("physics mutation tools validate before commit and prove fresh observed state", async () => {
  const force = await readFile(new URL("../unitypkg/Editor/Tools/physics/force.cs", import.meta.url), "utf8");
  assert.match(force, /ValidateFinite\(force, "force"\)/);
  assert.match(force, /Enum\.IsDefined\(typeof\(ForceMode\), mode\)/);
  assert.match(force, /Enum\.IsDefined\(typeof\(ForceApplicationType\), forceType\)/);
  assert.ok(force.indexOf("ValidateFinite(force") < force.indexOf("ctx.CommitEffect()"));
  assert.ok(force.indexOf("if (!requestedNonZero)") < force.indexOf("ctx.CommitEffect()"));
  assert.match(force, /rigidbody\.Sleep\(\)[\s\S]*if \(!rigidbody\.IsSleeping\(\)\)[\s\S]*rigidbody\.Add(?:Explosion)?Force/);
  assert.match(force, /if \(observedRigidbody\.IsSleeping\(\)\)/);
  assert.doesNotMatch(force, /Physics\.Simulate/);
  assert.match(force, /EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/);

  const assignMaterial = await readFile(new URL("../unitypkg/Editor/Tools/physics/assign-material.cs", import.meta.url), "utf8");
  assert.match(assignMaterial, /var observedTarget = dryRun \? target : EditorUtility\.InstanceIDToObject\(targetId\) as GameObject/);
  assert.match(assignMaterial, /var observedCollider = dryRun \? collider : EditorUtility\.InstanceIDToObject\(colliderId\) as Collider/);
  assert.match(force, /candidate\.GetInstanceID\(\) == rigidbodyId/);

  const jointRemove = await readFile(new URL("../unitypkg/Editor/Tools/physics/joint/remove.cs", import.meta.url), "utf8");
  assert.match(jointRemove, /requestedJointIds/);
  assert.match(jointRemove, /remainingIds\.Contains\(id\)/);
  assert.match(jointRemove, /E_PHYSICS_JOINT_REMOVE_STATE_MISMATCH/);

  const settings = await readFile(new URL("../unitypkg/Editor/Tools/physics/settings.cs", import.meta.url), "utf8");
  assert.match(settings, /private sealed class SettingPlan/);
  assert.match(settings, /var immutablePlans = plans\.AsReadOnly\(\)/);
  assert.match(settings, /if \(immutablePlans\.Count > 0\)\s*\{\s*ctx\.CommitEffect\(\)/);
  assert.match(settings, /Unsupported physics setting/);
  assert.match(settings, /bool\.TryParse/);
  assert.match(settings, /public string SimulationMode2D \{ get; set; \}/);
  assert.match(settings, /ParseSimulationMode2D\(request\.SimulationMode2D\)/);
  assert.match(settings, /Physics2D\.simulationMode = value/);
  assert.match(settings, /Physics2D\.simulationMode == value/);
  assert.ok(settings.indexOf("AddTypedPlans(request, plans);") < settings.indexOf("ctx.CommitEffect()"));
  assert.match(settings, /foreach \(var plan in immutablePlans\)\s*if \(!plan\.ReadbackMatches\(\)\)/);

  const simulation = await readFile(new URL("../unitypkg/Editor/Tools/physics/simulation.cs", import.meta.url), "utf8");
  const support = await readFile(new URL("../unitypkg/Editor/Tools/physics/PhysicsToolSupport.cs", import.meta.url), "utf8");
  assert.match(support, /SimulationMode2D = Physics2D\.simulationMode\.ToString\(\)/);
  assert.match(support, /Physics\.simulationMode != SimulationMode\.Script/);
  assert.match(support, /enabled \? SimulationMode\.FixedUpdate : SimulationMode\.Script/);
  assert.match(settings, /PhysicsToolSupport\.SetAutoSimulation3D\(value\)/);
  assert.match(simulation, /PhysicsToolSupport\.IsAutoSimulation3D\(\)/);
  assert.doesNotMatch(settings + simulation + support, /Physics\.autoSimulation = value|Physics\.autoSimulation == value|AutoSimulation = Physics\.autoSimulation|if \(Physics\.autoSimulation/);
});

test("physics.settings static catalog exposes the manual simulation contract", async () => {
  const catalog = JSON.parse(await readFile(new URL("../contracts/static-capability-catalog.json", import.meta.url), "utf8"));
  const descriptor = catalog.tools.find((tool: { name?: string }) => tool.name === "physics.settings");
  assert.ok(descriptor, "physics.settings must exist in the static catalog");
  assert.equal(descriptor.inputSchema.properties.simulationMode2D.type, "string");
  assert.equal(descriptor.outputSchema.$defs["UnityCli.PhysicsSettingsInfo"].properties.simulationMode2D.type, "string");
  assert.match(descriptor.description, /simulationMode2D accepts FixedUpdate, Update, or Script/);
});

test("physics bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/physics.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "target"\)/, "bridge should forward structured target");
  assert.match(spec, /gravity: structured\.gravity/, "bridge should forward the current structured gravity object");
  assert.match(spec, /gravity2d: structured\.gravity2d/, "bridge should forward the current structured gravity2d object");
  assert.match(spec, /simulationMode2D: stringFlag\(flags, "simulation-mode-2d"\) \?\? stringField\(flags, "simulationMode2D"\)/);
  assert.match(spec, /completionProbe: structured\.completionProbe/, "physics.simulation must keep its mandatory proof input");
  assert.doesNotMatch(spec, /gravityX:/, "removed vector component inputs must not re-enter the bridge payload");
  assert.match(spec, /boolField\(flags, "useGravity"\)/, "bridge should forward structured useGravity");
  assert.match(spec, /stringField\(flags, "shape"\)/, "bridge should forward structured shape");
  assert.match(spec, /stringField\(flags, "materialPath"\)/, "bridge should forward structured materialPath");
  assert.match(spec, /stringField\(flags, "connectedBody"\)/, "bridge should forward structured joint connected body");
  assert.doesNotMatch(spec, /stringField\(flags, "action"\)/, "bridge must not forward a joint action router field");
});
