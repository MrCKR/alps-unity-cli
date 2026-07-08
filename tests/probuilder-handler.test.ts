import assert from "node:assert/strict";
import test from "node:test";
import { commandContracts, findCommandContract } from "../src/contracts/command-contract.js";
import { builtinExtensionToolContributions } from "../src/extensions/tool-manifest.js";
import { probuilderDeleteFacesTool, probuilderPingTool } from "./helpers/live-tool-fixtures.js";
import { discoverLiveTools } from "./helpers/csharp-test-helpers.js";
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

const REPRESENTATIVE_CHAINS = [
  "probuilder.create-shape",
  "probuilder.mesh-info",
  "probuilder.extrude-faces",
  "probuilder.set-face-material",
  "probuilder.validate-mesh",
  "probuilder.repair-mesh"
];

test("all 36 ProBuilder contracts have csharp_handler = present", () => {
  const contracts = commandContracts().filter((entry) => entry.group === "probuilder");
  assert.equal(contracts.length, 36);
  for (const contract of contracts) {
    assert.equal(contract.csharp_handler, "present");
  }
});

test("all 36 ProBuilder tools are discoverable through extension manifest contributions", () => {
  const names = builtinExtensionToolContributions()
    .filter((tool) => tool.extensionId === "probuilder")
    .map((tool) => tool.name)
    .sort();
  assert.deepEqual(names, [...EXPECTED_PROBUILDER_TOOLS].sort());
});

test("representative chains keep contract routing with present C# handlers", () => {
  for (const name of REPRESENTATIVE_CHAINS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name}: contract must exist`);
    assert.equal(contract?.csharp_handler, "present");
    assert.equal(contract?.handler_type, "bridge");
  }
});

test("unavailable degradation comes from manifest/live descriptor, not offline metadata", async () => {
  const contributions = builtinExtensionToolContributions().filter((tool) => tool.extensionId === "probuilder");
  for (const tool of contributions) {
    assert.deepEqual(tool.requiresPackages, ["com.unity.probuilder"], `${tool.name}: requiresPackages must include com.unity.probuilder`);
  }

  const liveTools = await discoverLiveTools(probuilderPingTool(), probuilderDeleteFacesTool());
  const ping = liveTools.get("probuilder.ping")!;
  assert.equal(ping.destructiveHint, false);
  assert.deepEqual(ping.requiresPackages, ["com.unity.probuilder"]);

  const deleteFaces = liveTools.get("probuilder.delete-faces")!;
  assert.equal(deleteFaces.destructiveHint, true);
  assert.equal(deleteFaces.supportsDryRun, true);
  assert.deepEqual(deleteFaces.requiresPackages, ["com.unity.probuilder"]);
});

test("ProBuilder manifest count matches contract count", () => {
  const contributions = builtinExtensionToolContributions().filter((tool) => tool.extensionId === "probuilder");
  const contracts = commandContracts().filter((entry) => entry.group === "probuilder");
  assert.equal(contributions.length, contracts.length);
  assert.equal(contributions.length, 36);
});
