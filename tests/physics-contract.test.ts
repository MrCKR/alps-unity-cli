import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile, readdir } from "node:fs/promises";

/** 递归列举 .cs 文件。 */
async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

/** 读取整个 Physics 域新形态源码集合。 */
async function readPhysicsSources(): Promise<string> {
  const toolsUrl = new URL("../unitypkg/Editor/Tools/physics/", import.meta.url);
  const files = await listCsFiles(toolsUrl);
  return (await Promise.all(files.map((file) => readFile(file, "utf8")))).join("\n");
}

const PHYSICS_COMMANDS = ["physics.settings", "physics.collision-matrix", "physics.material", "physics.assign-material", "physics.joint", "physics.force", "physics.overlap", "physics.rigidbody", "physics.raycast", "physics.simulation", "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate"];

/**
 * Physics 域契约测试（14 工具）。
 * 对齐 CoplayDev manage_physics，验证 metadata/bridge/contract/C# 一致，
 * physics.validate 业务字段不被 envelope 吞掉，query/force/simulation 的 Play Mode 语义。
 */

test("physics group exposes the 14 tools", () => {
  const tools = filterToolsByGroup("physics").map((t) => t.name).sort();
  assert.equal(tools.length, 14);
  for (const name of PHYSICS_COMMANDS) {
    assert.ok(tools.includes(name), `${name} should exist`);
  }
});

test("physics query tools are supported/read-only", () => {
  for (const name of ["physics.raycast", "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.overlap", "physics.validate"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.examples?.length, `${name} examples`);
  }
});

test("physics runtime tools (force/simulation) are runtime-only", () => {
  const force = findToolByName("physics.force")!;
  assert.equal(force.playModeSupport, "runtime-only", "force is runtime-only");
  assert.ok(force.sideEffects?.includes("runtime-state"));
  const sim = findToolByName("physics.simulation")!;
  assert.equal(sim.playModeSupport, "runtime-only", "simulation is runtime-only");
});

test("physics write tools are session-affecting", () => {
  for (const name of ["physics.settings", "physics.collision-matrix", "physics.material", "physics.assign-material", "physics.joint", "physics.rigidbody"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} session-affecting`);
    assert.ok(tool.sideEffects?.length, `${name} side effects`);
  }
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
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for physics commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of PHYSICS_COMMANDS) {
    const hasRegisterCommand = allSource.includes(`RegisterCommand(registry, "${command}"`);
    const hasIUnityCliTool = allSource.includes(`Name = "${command}"`);
    assert.ok(hasRegisterCommand || hasIUnityCliTool, `${command} should be registered via RegisterCommand or IUnityCliTool`);
  }
});

test("physics C# has no legacy unity_cli command form", async () => {
  const source = await readPhysicsSources();
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

test("M absorption fields are described for AI self-discovery", async () => {
  const collision = findToolByName("physics.collision-matrix")!;
  assert.equal(Array.isArray(collision.inputSchema.required), false, "full matrix read must not require layerA/layerB");
  assert.match(collision.description, /full 32-layer collision matrix/i);

  const assign = findToolByName("physics.assign-material")!;
  assert.equal(assign.destructiveHint, false);
  assert.equal(assign.supportsDryRun, true);
  assert.ok(assign.sideEffects?.includes("scene-modify"));
  assert.deepEqual(assign.inputSchema.required, ["target", "materialPath"]);

  const joint = findToolByName("physics.joint")!;
  assert.ok((joint.inputSchema.properties as Record<string, unknown>).action, "joint action should be documented");
  assert.match(joint.description, /action=remove/);

  const overlap = findToolByName("physics.overlap")!;
  assert.match(overlap.description, /mode 2d/i);
  const shapecast = findToolByName("physics.shapecast")!;
  assert.match(shapecast.description, /CircleCast\/BoxCast\/CapsuleCast/);

  const rigidbody = findToolByName("physics.rigidbody")!;
  assert.match(rigidbody.description, /without adding or modifying components/);
});
