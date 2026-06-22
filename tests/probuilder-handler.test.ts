/**
 * ProBuilder C# handler contract tests.
 *
 * Validates:
 * - All 36 ProBuilder tools have csharp_handler = "present" in contract
 * - Tool names match the 36 C# handler classes
 * - Unavailable degradation: contract entries require com.unity.probuilder
 * - 6 representative chain names exist
 */

import assert from "node:assert/strict";
import test from "node:test";
import { commandContracts, findCommandContract } from "../src/contracts/command-contract.js";
import { filterToolsByGroup, findToolByName } from "../src/tools/metadata/index.js";
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

// 6 representative chains per acceptance criteria
const REPRESENTATIVE_CHAINS = [
  "probuilder.create-shape",
  "probuilder.mesh-info",
  "probuilder.extrude-faces",
  "probuilder.set-face-material",
  "probuilder.validate-mesh",
  "probuilder.repair-mesh"
];

test("all 36 ProBuilder contracts have csharp_handler = present", () => {
  const contracts = commandContracts().filter(c => c.group === "probuilder");
  assert.equal(contracts.length, 36, `Expected 36 probuilder contracts, got ${contracts.length}`);

  for (const contract of contracts) {
    assert.equal(
      contract.csharp_handler,
      "present",
      `${contract.bridgeCommand}: csharp_handler should be "present" after T2b implementation, got "${contract.csharp_handler}"`
    );
  }
});

test("all 36 ProBuilder contracts require com.unity.probuilder package", () => {
  const contracts = commandContracts().filter(c => c.group === "probuilder");

  for (const contract of contracts) {
    assert.ok(
      contract.requires_packages,
      `${contract.bridgeCommand}: must have requires_packages`
    );
    assert.deepEqual(
      contract.requires_packages,
      ["com.unity.probuilder"],
      `${contract.bridgeCommand}: requires_packages must be ["com.unity.probuilder"]`
    );
  }
});

test("all 36 ProBuilder tools are discoverable via metadata", () => {
  for (const name of EXPECTED_PROBUILDER_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name}: should be discoverable via findToolByName`);
    assert.equal(tool.group, "probuilder");
    assert.equal(tool.handler, "hybrid");
    assert.equal(tool.target, "editor");
    assert.equal(tool.availabilityScope, "editorSession");
  }
});

test("6 representative chains exist with correct handler = hybrid and csharp_handler = present", () => {
  for (const name of REPRESENTATIVE_CHAINS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name}: representative chain tool must exist`);
    assert.equal(tool.handler, "hybrid", `${name}: handler must be hybrid`);

    const contract = findCommandContract(name);
    assert.ok(contract, `${name}: contract must exist`);
    assert.equal(contract.csharp_handler, "present", `${name}: csharp_handler must be present`);
    assert.equal(contract.handler_type, "hybrid", `${name}: handler_type must be hybrid`);
  }
});

test("unavailable degradation: all ProBuilder tools have requiresPackages with com.unity.probuilder", () => {
  const probuilderTools = filterToolsByGroup("probuilder");

  for (const tool of probuilderTools) {
    assert.ok(
      tool.requiresPackages,
      `${tool.name}: must have requiresPackages for unavailable degradation`
    );
    assert.deepEqual(
      tool.requiresPackages,
      ["com.unity.probuilder"],
      `${tool.name}: requiresPackages must include com.unity.probuilder`
    );
  }
});

test("unavailable degradation: ProBuilder ping tool is read-only and safe to call without package", () => {
  const ping = findToolByName("probuilder.ping");
  assert.ok(ping, "probuilder.ping must exist");
  assert.equal(ping.readOnlyHint, true, "ping must be read-only");
  assert.equal(ping.destructiveHint, false, "ping must not be destructive");
  // Ping is the canonical unavailable-check tool; it returns installed=false
  // without throwing when the package is missing.
});

test("ProBuilder tool count in metadata matches contract count", () => {
  const tools = filterToolsByGroup("probuilder");
  const contracts = commandContracts().filter(c => c.group === "probuilder");
  assert.equal(tools.length, contracts.length, "tool count and contract count must match");
  assert.equal(tools.length, 36, "both must be 36");
});
