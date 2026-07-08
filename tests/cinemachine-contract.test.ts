import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
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

test("Cinemachine bridge specs route through project-tool.run with toolName", () => {
  const specs = bridgeCommandRegistry.specs().filter((spec) => spec.group === "cinemachine");
  assert.equal(specs.length, 13);
  assert.ok(specs.every((spec) => spec.bridgeCommand === "project-tool.run"));
  assert.ok(specs.every((spec) => spec.toolsOnly === true));
  assert.deepEqual(specs.map((spec) => spec.toolName).sort(), [...EXPECTED_CINEMACHINE_TOOLS].sort());

  const forceSpec = specs.find((spec) => spec.command === "force-camera");
  assert.equal(forceSpec?.toolName, "cinemachine.force-camera");

  const releaseSpec = specs.find((spec) => spec.command === "release-override");
  assert.equal(releaseSpec?.toolName, "cinemachine.release-override");
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

test("Cinemachine C# extension is self-contained and no longer uses legacy Commands registration", () => {
  assert.equal(existsSync("extensions/cinemachine/unitypkg/Editor/Commands/CinemachineCommands.cs"), false);

  const registrySource = readFileSync("unitypkg/Editor/Bridge/BridgeCommandRegistry.cs", "utf8");
  assert.doesNotMatch(registrySource, /RegisterCinemachineCommands/);

  const supportSource = readFileSync("extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineToolSupport.cs", "utf8");
  assert.match(supportSource, /FindSetCameraOverrideMethod/);
  assert.match(supportSource, /FindReleaseCameraOverrideMethod/);
  assert.match(supportSource, /FindPublicInstanceMethod\(Type ownerType, string methodName, params Type\[] parameterTypes\)/);
  assert.match(supportSource, /GetPipelineComponent\(Component camera, string stage\)/);
  assert.match(supportSource, /ForceOverrideAvailable/);

  const expectedFiles = [
    "ping.cs",
    "create.cs",
    "target.cs",
    "lens.cs",
    "priority.cs",
    "blend.cs",
    "body.cs",
    "aim.cs",
    "noise.cs",
    "brain.cs",
    "extension.cs",
    "force-camera.cs",
    "release-override.cs"
  ];
  for (const file of expectedFiles) {
    assert.equal(existsSync(`extensions/cinemachine/unitypkg/Editor/Cinemachine/${file}`), true, `${file} should exist`);
  }
});
