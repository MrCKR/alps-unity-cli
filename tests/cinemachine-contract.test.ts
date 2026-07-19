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
  "cinemachine.brain.ensure",
  "cinemachine.brain.status",
  "cinemachine.extension.list",
  "cinemachine.extension.add",
  "cinemachine.extension.remove"
] as const;

test("Cinemachine extension manifest exposes the expected single-action tools", () => {
  const contributionNames = builtinExtensionToolContributions()
    .filter((tool) => tool.extensionId === "cinemachine")
    .map((tool) => tool.name)
    .sort();
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
  assert.deepEqual(specs.map((spec) => spec.toolName).sort(), [...EXPECTED_CINEMACHINE_TOOLS].sort());
  assert.ok(specs.every((spec) => spec.bridgeCommand === "project-tool.run"));
  assert.ok(specs.every((spec) => spec.toolsOnly === true));
});

test("Cinemachine tools have matching command contracts", () => {
  for (const name of EXPECTED_CINEMACHINE_TOOLS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name}: contract not found`);
    assert.equal(contract?.group, "cinemachine", `${name}: contract group mismatch`);
    assert.equal(contract?.handler_type, name === "cinemachine.create" ? "hybrid" : "bridge", `${name}: contract handler mismatch`);
    assert.equal(contract?.csharp_handler, "present", `${name}: C# handler should be present`);
    assert.equal(contract?.destructive, false, `${name}: Cinemachine session operations must be non-destructive`);
  }
});

test("Cinemachine extension uses the declared CM2 and CM3 package APIs", () => {
  assert.equal(existsSync("extensions/cinemachine/unitypkg/Editor/Commands/CinemachineCommands.cs"), false);
  assert.equal(existsSync("extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineCm2.cs"), false);

  const registrySource = readFileSync("unitypkg/Editor/Bridge/BridgeCommandRegistry.cs", "utf8");
  assert.doesNotMatch(registrySource, /RegisterCinemachineCommands/);

  const supportSource = readFileSync("extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineToolSupport.cs", "utf8");
  assert.match(supportSource, /Unity\.Cinemachine\.CinemachineCamera/);
  assert.match(supportSource, /Cinemachine\.CinemachineVirtualCamera/);
  assert.match(supportSource, /ComponentTypeAliases/);
  assert.match(supportSource, /AddCinemachineComponent/);
  assert.match(supportSource, /GetPipelineComponent\(Component camera, string stage\)/);
  assert.match(supportSource, /RequirePipelineComponentStage/);
  assert.match(supportSource, /"Cinemachine\.CinemachineHardLookAt"/);

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
    "brain-ensure.cs",
    "brain-status.cs",
    "extension-list.cs",
    "extension-add.cs",
    "extension-remove.cs"
  ];
  for (const file of expectedFiles) {
    assert.equal(existsSync(`extensions/cinemachine/unitypkg/Editor/Cinemachine/${file}`), true, `${file} should exist`);
  }
});

test("cinemachine.create rejects an unsupported preset before scene mutation", () => {
  const source = readFileSync("extensions/cinemachine/unitypkg/Editor/Cinemachine/create.cs", "utf8");
  const validationIndex = source.indexOf("E_CINEMACHINE_PRESET_INVALID");
  const mutationIndex = source.indexOf("new GameObject(cameraName)");
  assert.ok(validationIndex >= 0, "invalid preset must return a stable error code");
  assert.ok(mutationIndex > validationIndex, "preset validation must precede GameObject creation");
  assert.match(source, /Pass one of: follow, third_person, freelook, dolly, static, top_down, side_scroller/);
});
