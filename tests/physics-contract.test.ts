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

const PHYSICS_COMMANDS = ["physics.settings", "physics.collision-matrix", "physics.material", "physics.joint", "physics.force", "physics.overlap", "physics.rigidbody", "physics.raycast", "physics.simulation", "physics.raycast-all", "physics.linecast", "physics.shapecast", "physics.validate"];

/**
 * Physics 域契约测试（13 工具）。
 * 对齐 CoplayDev manage_physics，验证 metadata/bridge/contract/C# 一致，
 * physics.validate 业务字段不被 envelope 吞掉，query/force/simulation 的 Play Mode 语义。
 */

test("physics group exposes the 13 tools", () => {
  const tools = filterToolsByGroup("physics").map((t) => t.name).sort();
  assert.equal(tools.length, 13);
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
  for (const name of ["physics.settings", "physics.collision-matrix", "physics.material", "physics.joint", "physics.rigidbody"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} session-affecting`);
    assert.ok(tool.sideEffects?.length, `${name} side effects`);
  }
});

test("bridge specs resolve for physics tools", () => {
  for (const [cmd, expected] of [["settings", "physics.settings"], ["collision-matrix", "physics.collision-matrix"], ["material", "physics.material"], ["joint", "physics.joint"], ["force", "physics.force"], ["overlap", "physics.overlap"], ["rigidbody", "physics.rigidbody"], ["raycast", "physics.raycast"], ["simulation", "physics.simulation"], ["raycast-all", "physics.raycast-all"], ["linecast", "physics.linecast"], ["shapecast", "physics.shapecast"], ["validate", "physics.validate"]] as const) {
    const spec = bridgeCommandRegistry.lookup("physics", cmd);
    assert.equal(spec?.bridgeCommand, expected, `physics ${cmd} should resolve`);
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
    assert.ok(allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should be registered`);
  }
});

test("physics C# has no legacy unity_cli command form", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/PhysicsCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli "), false, "must not reference legacy 'unity_cli ' command form");
});

test("physics.validate C# wraps result in SuccessJson so business fields survive the envelope", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/PhysicsCommands.cs", import.meta.url), "utf8");
  const block = source.slice(source.indexOf("PhysicsValidate(BridgeRequest request)"), source.indexOf("private static string PhysicsRaycast("));
  assert.match(block, /return SuccessJson\(w\.EndObject/, "PhysicsValidate must wrap payload in SuccessJson");
});

test("physics bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/physics.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "target"\)/, "bridge should forward structured target");
  assert.match(spec, /numberField\(flags, "gravityX"\)/, "bridge should forward structured gravityX");
  assert.match(spec, /boolField\(flags, "useGravity"\)/, "bridge should forward structured useGravity");
  assert.match(spec, /stringField\(flags, "shape"\)/, "bridge should forward structured shape");
});
