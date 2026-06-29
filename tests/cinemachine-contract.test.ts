import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { filterToolsByGroup, findToolByName } from "../src/tools/metadata/index.js";
import "../src/extensions/index.js";

const EXPECTED_CINEMACHINE_TOOLS = [
  "cinemachine.ping",
  "cinemachine.create",
  "cinemachine.target",
  "cinemachine.lens",
  "cinemachine.priority",
  "cinemachine.blend",
  "cinemachine.body",
  "cinemachine.aim",
  "cinemachine.noise",
  "cinemachine.brain",
  "cinemachine.force-camera",
  "cinemachine.release-override",
  "cinemachine.extension"
] as const;

const SESSION_OVERRIDE_TOOLS = new Set([
  "cinemachine.force-camera",
  "cinemachine.release-override"
]);

test("Cinemachine extension exposes exactly 13 single-action tools", () => {
  const tools = filterToolsByGroup("cinemachine");
  assert.equal(tools.length, 13);
  assert.deepEqual(tools.map((tool) => tool.name).sort(), [...EXPECTED_CINEMACHINE_TOOLS].sort());
});

test("Cinemachine metadata documents package gate and session override semantics", () => {
  for (const name of EXPECTED_CINEMACHINE_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name}: tool should be discoverable`);
    assert.equal(tool.group, "cinemachine", `${name}: group mismatch`);
    assert.equal(tool.handler, "hybrid", `${name}: handler must be hybrid`);
    assert.equal(tool.source, "extension", `${name}: source must be extension`);
    assert.equal(tool.target, "editor", `${name}: target must be editor`);
    assert.equal(tool.availabilityScope, "editorSession", `${name}: availabilityScope mismatch`);
    assert.deepEqual(tool.requiresPackages, ["com.unity.cinemachine"], `${name}: requiresPackages mismatch`);
    assert.equal(tool.cliCommand, `tools run ${name}`, `${name}: cliCommand must use tools run`);

    if (name === "cinemachine.extension") {
      assert.equal(tool.destructiveHint, true, `${name}: extension add/remove remains destructive`);
    } else {
      assert.equal(tool.destructiveHint, false, `${name}: should not require --yes`);
    }

    if (SESSION_OVERRIDE_TOOLS.has(name)) {
      assert.equal(tool.playModeSupport, "supported", `${name}: playModeSupport should be explicit`);
      assert.ok(tool.sideEffects?.includes("editor-session-state"), `${name}: should disclose session side effect`);
    }
  }
});

test("Cinemachine bridge specs match tool metadata", () => {
  const specs = bridgeCommandRegistry.specs().filter((spec) => spec.group === "cinemachine");
  assert.equal(specs.length, 13);
  const specNames = specs.map((spec) => spec.bridgeCommand).sort();
  assert.deepEqual(specNames, [...EXPECTED_CINEMACHINE_TOOLS].sort());

  const forceSpec = specs.find((spec) => spec.bridgeCommand === "cinemachine.force-camera");
  assert.ok(forceSpec);
  assert.equal(forceSpec.command, "force-camera");
  assert.equal(forceSpec.toolsOnly, true);

  const releaseSpec = specs.find((spec) => spec.bridgeCommand === "cinemachine.release-override");
  assert.ok(releaseSpec);
  assert.equal(releaseSpec.command, "release-override");
  assert.equal(releaseSpec.toolsOnly, true);
});

test("Cinemachine tools have matching command contracts", () => {
  for (const name of EXPECTED_CINEMACHINE_TOOLS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name}: contract not found`);
    assert.equal(contract.group, "cinemachine", `${name}: contract group mismatch`);
    assert.equal(contract.handler_type, "hybrid", `${name}: contract handler mismatch`);
    assert.equal(contract.csharp_handler, "present", `${name}: C# handler should be present`);
    assert.deepEqual(contract.requires_packages, ["com.unity.cinemachine"], `${name}: package gate mismatch`);
    assert.equal(contract.destructive, name === "cinemachine.extension", `${name}: destructive contract mismatch`);
  }
});

test("Cinemachine C# extension registers ping and Brain override commands", () => {
  const source = readFileSync("extensions/cinemachine/unitypkg/Editor/Commands/CinemachineCommands.cs", "utf8");
  assert.match(source, /RegisterCommand\(registry, "cinemachine\.ping",\s+"cinemachine", false, CinemachinePing\)/);
  assert.match(source, /RegisterCommand\(registry, "cinemachine\.force-camera",\s+"cinemachine", false, CinemachineForceCamera\)/);
  assert.match(source, /RegisterCommand\(registry, "cinemachine\.release-override",\s+"cinemachine", false, CinemachineReleaseOverride\)/);
  assert.match(source, /FindSetCameraOverrideMethod\(brain\.GetType\(\)\)/);
  assert.match(source, /FindReleaseCameraOverrideMethod\(brain\.GetType\(\)\)/);
  assert.match(source, /FindPublicInstanceMethod\(_cmCameraType, "GetCinemachineComponent", stageEnumType\)/);
  assert.match(source, /Prop\("overrideId", \(long\)newOverrideId\)/);
  assert.match(source, /forceOverrideAvailable/);
});
