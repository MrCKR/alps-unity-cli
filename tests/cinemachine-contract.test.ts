import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { builtinExtensionMetadata } from "../src/extensions/metadata/index.js";
import { builtinExtensionToolContributions, findBuiltinExtensionToolContribution } from "../src/extensions/tool-manifest.js";
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

test("Cinemachine extension manifest exposes exactly 13 single-action tools", () => {
  const contributionNames = builtinExtensionToolContributions()
    .filter((tool) => tool.extensionId === "cinemachine")
    .map((tool) => tool.name)
    .sort();
  assert.equal(contributionNames.length, 13);
  assert.deepEqual(contributionNames, [...EXPECTED_CINEMACHINE_TOOLS].sort());

  const manifest = builtinExtensionMetadata().find((entry) => entry.id === "cinemachine");
  assert.ok(manifest);
  assert.deepEqual([...(manifest?.providesTools ?? [])].sort(), [...EXPECTED_CINEMACHINE_TOOLS].sort());
});

test("Cinemachine manifest contributions document package gate and extension ownership", () => {
  for (const name of EXPECTED_CINEMACHINE_TOOLS) {
    const contribution = findBuiltinExtensionToolContribution(name);
    assert.ok(contribution, `${name}: tool should be contributed by an extension`);
    assert.equal(contribution?.extensionId, "cinemachine", `${name}: extension mismatch`);
    assert.equal(contribution?.group, "cinemachine", `${name}: group mismatch`);
    assert.deepEqual(contribution?.requiresPackages, ["com.unity.cinemachine"], `${name}: package gate mismatch`);
  }
});

test("Cinemachine bridge specs match extension tool names", () => {
  const specs = bridgeCommandRegistry.specs().filter((spec) => spec.group === "cinemachine");
  assert.equal(specs.length, 13);
  const specNames = specs.map((spec) => spec.bridgeCommand).sort();
  assert.deepEqual(specNames, [...EXPECTED_CINEMACHINE_TOOLS].sort());

  const forceSpec = specs.find((spec) => spec.bridgeCommand === "cinemachine.force-camera");
  assert.ok(forceSpec);
  assert.equal(forceSpec?.command, "force-camera");
  assert.equal(forceSpec?.toolsOnly, true);

  const releaseSpec = specs.find((spec) => spec.bridgeCommand === "cinemachine.release-override");
  assert.ok(releaseSpec);
  assert.equal(releaseSpec?.command, "release-override");
  assert.equal(releaseSpec?.toolsOnly, true);
});

test("Cinemachine tools have matching command contracts", () => {
  for (const name of EXPECTED_CINEMACHINE_TOOLS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name}: contract not found`);
    assert.equal(contract?.group, "cinemachine", `${name}: contract group mismatch`);
    assert.equal(contract?.handler_type, name === "cinemachine.create" ? "hybrid" : "bridge", `${name}: contract handler mismatch`);
    assert.equal(contract?.csharp_handler, "present", `${name}: C# handler should be present`);
    assert.equal(contract?.destructive, name === "cinemachine.extension", `${name}: destructive contract mismatch`);
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

  for (const name of EXPECTED_CINEMACHINE_TOOLS) {
    assert.ok(source.includes(`RegisterCommand(registry, "${name}"`) || source.includes(`"${name}"`), `${name} should appear in extension source`);
  }

  for (const name of SESSION_OVERRIDE_TOOLS) {
    assert.ok(source.includes(name), `${name} should keep session override implementation in source`);
  }
});
