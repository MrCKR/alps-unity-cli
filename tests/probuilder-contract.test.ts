import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { commandContracts, findCommandContract } from "../src/contracts/command-contract.js";
import { builtinExtensionMetadata } from "../src/extensions/metadata/index.js";
import { builtinExtensionToolContributions } from "../src/extensions/tool-manifest.js";
import "../src/extensions/index.js";

const EXPECTED_PROBUILDER_TOOLS = [
  "probuilder.ping",
  "probuilder.create-shape",
  "probuilder.create-poly-shape",
  "probuilder.extrude-faces",
  "probuilder.delete-faces",
  "probuilder.detach-faces",
  "probuilder.merge-faces",
  "probuilder.subdivide",
  "probuilder.flip-normals",
  "probuilder.select-faces",
  "probuilder.set-face-material",
  "probuilder.set-face-color",
  "probuilder.set-face-uvs",
  "probuilder.extrude-edges",
  "probuilder.bevel-edges",
  "probuilder.bridge-edges",
  "probuilder.connect-elements",
  "probuilder.combine-meshes",
  "probuilder.merge-objects",
  "probuilder.duplicate-and-flip",
  "probuilder.create-polygon",
  "probuilder.merge-vertices",
  "probuilder.weld-vertices",
  "probuilder.split-vertices",
  "probuilder.move-vertices",
  "probuilder.insert-vertex",
  "probuilder.append-vertices-to-edge",
  "probuilder.mesh-info",
  "probuilder.validate-mesh",
  "probuilder.convert-to-probuilder",
  "probuilder.set-smoothing",
  "probuilder.auto-smooth",
  "probuilder.center-pivot",
  "probuilder.freeze-transform",
  "probuilder.set-pivot",
  "probuilder.repair-mesh"
] as const;

const DESTRUCTIVE_TOOLS = new Set([
  "probuilder.delete-faces",
  "probuilder.combine-meshes",
  "probuilder.merge-objects"
]);

test("ProBuilder extension manifest has exactly 36 tools", () => {
  const contributionNames = builtinExtensionToolContributions()
    .filter((tool) => tool.extensionId === "probuilder")
    .map((tool) => tool.name)
    .sort();
  assert.equal(contributionNames.length, 36, `Expected 36 probuilder tools, got ${contributionNames.length}`);
  assert.deepEqual(contributionNames, [...EXPECTED_PROBUILDER_TOOLS].sort());

  const manifest = builtinExtensionMetadata().find((entry) => entry.id === "probuilder");
  assert.ok(manifest);
  assert.deepEqual([...(manifest?.providesTools ?? [])].sort(), [...EXPECTED_PROBUILDER_TOOLS].sort());
});

test("every ProBuilder tool has matching extension contribution metadata", () => {
  const contributions = builtinExtensionToolContributions().filter((tool) => tool.extensionId === "probuilder");

  for (const tool of contributions) {
    assert.equal(tool.group, "probuilder", `${tool.name}: group must be probuilder`);
    assert.equal(tool.extensionId, "probuilder", `${tool.name}: extension ownership mismatch`);
    assert.deepEqual(tool.requiresPackages, ["com.unity.probuilder"], `${tool.name}: requiresPackages must be [\"com.unity.probuilder\"]`);
  }
});

test("every ProBuilder tool has a matching bridge spec", () => {
  const probuilderSpecs = bridgeCommandRegistry.specs().filter((spec) => spec.group === "probuilder");
  assert.equal(probuilderSpecs.length, 36, `Expected 36 probuilder bridge specs, got ${probuilderSpecs.length}`);

  for (const spec of probuilderSpecs) {
    assert.equal(spec.bridgeCommand, "project-tool.run", `${spec.group}.${spec.command}: ProBuilder bridgeCommand must be project-tool.run`);
    assert.ok(spec.toolName, `${spec.group}.${spec.command}: ProBuilder spec must carry toolName`);
  }

  const specToolNames = new Set(probuilderSpecs.map((spec) => spec.toolName!));
  assert.deepEqual(specToolNames, new Set(EXPECTED_PROBUILDER_TOOLS));
});

test("every ProBuilder tool has a matching command contract", () => {
  const probuilderSpecs = bridgeCommandRegistry.specs().filter((spec) => spec.group === "probuilder");
  const handlerByTool = new Map(probuilderSpecs.map((spec) => [spec.toolName!, spec.supportsJob ? "hybrid" : "bridge"] as const));

  for (const name of EXPECTED_PROBUILDER_TOOLS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name}: contract not found`);
    assert.equal(contract?.group, "probuilder", `${name}: contract group must be probuilder`);
    assert.equal(contract?.handler_type, handlerByTool.get(name), `${name}: contract handler_type mismatch`);
    assert.equal(contract?.csharp_handler, "present", `${name}: contract csharp_handler must be present`);
    assert.equal(typeof contract?.destructive, "boolean", `${name}: contract destructive flag should stay boolean`);
  }
});

test("ProBuilder contract entries count is 36", () => {
  const contracts = commandContracts().filter((entry) => entry.group === "probuilder");
  assert.equal(contracts.length, 36, `Expected 36 probuilder contract entries, got ${contracts.length}`);
  assert.deepEqual(new Set(contracts.map((entry) => entry.bridgeCommand)), new Set(EXPECTED_PROBUILDER_TOOLS));
});

test("old ProBuilder tool names no longer exist in extension manifests", () => {
  const contributionNames = new Set(
    builtinExtensionToolContributions()
      .filter((tool) => tool.extensionId === "probuilder")
      .map((tool) => tool.name)
  );
  const oldNames = ["probuilder.shape", "probuilder.create", "probuilder.edit", "probuilder.csg"];
  for (const name of oldNames) {
    assert.equal(contributionNames.has(name), false, `${name}: old coarse-grained tool should NOT exist`);
  }
});

test("ProBuilder C# extension source contains representative public tool names", async () => {
  const toolFiles = await Promise.all([
    "ping.cs",
    "create-shape.cs",
    "extrude-faces.cs",
    "set-face-material.cs",
    "repair-mesh.cs"
  ].map(async (file) => readFile(new URL(`../extensions/probuilder/unitypkg/Editor/ProBuilder/${file}`, import.meta.url), "utf8")));
  const allSource = toolFiles.join("\n");

  for (const name of ["probuilder.ping", "probuilder.create-shape", "probuilder.extrude-faces", "probuilder.set-face-material", "probuilder.repair-mesh"]) {
    assert.ok(allSource.includes(name), `${name}: public tool name should appear in extension source`);
  }
});

test("ProBuilder scene context is implemented as read-only IUnityCliContext", async () => {
  const contextSource = await readFile(new URL("../extensions/probuilder/unitypkg/Editor/ProBuilder/ProBuilderSceneContext.cs", import.meta.url), "utf8");

  assert.match(contextSource, /class ProBuilderSceneContext : IUnityCliContext<ProBuilderSceneContext\.Request, ProBuilderSceneContext\.Result>/);
  assert.match(contextSource, /"probuilder\.scene"/);
  assert.match(contextSource, /Category\s*=\s*"probuilder"/);
  assert.match(contextSource, /RequiresPackages\s*=\s*new\[\] \{ "com\.unity\.probuilder" \}/);
  assert.match(contextSource, /public Result Read\(Request request, UnityCliProjectToolContext ctx\)/);
  assert.doesNotMatch(contextSource, /\[UnityCliTool\(/);
  assert.doesNotMatch(contextSource, /\[UnityCliContext\(/);
  assert.doesNotMatch(contextSource, /public static string Execute\(/);
  assert.doesNotMatch(contextSource, /public static string Run\(/);

  const bannedTokens = [
    "ExecuteMenuItem",
    "SaveAssets",
    "AssetDatabase.Refresh",
    "isPlaying =",
    "EditorSceneManager.SaveScene",
    "MarkSceneDirty",
    "PackageManager.",
    "File.Write",
    "File.WriteAll",
    "File.Append",
    "Directory.Create",
    "Directory.Delete",
    "Process.Start"
  ];
  for (const token of bannedTokens) {
    assert.doesNotMatch(contextSource, new RegExp(token.replace(".", "\\.")), `ProBuilder scene context must not contain source-guard token ${token}`);
  }
});
