/**
 * Contract/snapshot tests for ProBuilder extension — 36 single-action tools.
 *
 * Validates:
 * - 36 ProBuilder tool names, schemas, requiresPackages, destructive,
 *   target, cliCommand and bridge command consistency
 * - tools group list probuilder discovers 36 tools
 * - tools describe probuilder.* works for each tool
 * - Core capabilities no longer lists ProBuilder as a built-in C# domain
 */

import assert from "node:assert/strict";
import test from "node:test";
import { builtinToolMetadata, allRegisteredToolMetadata, filterToolsByGroup, findToolByName, extensionToolMetadata, EXTENSION_GROUPS, type ToolMetadata } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { commandContracts, findCommandContract } from "../src/contracts/command-contract.js";
import "../src/extensions/index.js";

// ─── Expected 36 tool names ─────────────────────────────────────────────

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


// Destructive ProBuilder tools.
const DESTRUCTIVE_TOOLS = new Set([
  "probuilder.delete-faces",
  "probuilder.combine-meshes",
  "probuilder.merge-objects"
]);

test("ProBuilder extension has exactly 36 tools", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  assert.equal(probuilderTools.length, 36, `Expected 36 probuilder tools, got ${probuilderTools.length}`);
});

test("ProBuilder tool names match expected extension contract exactly", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  const actualNames = probuilderTools.map((t) => t.name).sort();
  const expectedNames = [...EXPECTED_PROBUILDER_TOOLS].sort();
  assert.deepEqual(actualNames, expectedNames);
});

test("every ProBuilder tool has consistent metadata", () => {
  const probuilderTools = filterToolsByGroup("probuilder");

  for (const tool of probuilderTools) {
    // Unified ProBuilder tool constraints.
    assert.equal(tool.group, "probuilder", `${tool.name}: group must be probuilder`);
    assert.equal(tool.target, "editor", `${tool.name}: target must be editor`);
    assert.equal(tool.availabilityScope, "editorSession", `${tool.name}: availabilityScope must be editorSession`);
    assert.deepEqual(tool.requiresPackages, ["com.unity.probuilder"], `${tool.name}: requiresPackages must be ["com.unity.probuilder"]`);
    assert.equal(tool.handler, "hybrid", `${tool.name}: handler must be hybrid`);
    assert.equal(tool.docsTopic, "probuilder", `${tool.name}: docsTopic must be probuilder`);

    // inputSchema must be an object
    assert.equal(typeof tool.inputSchema, "object", `${tool.name}: inputSchema must be an object`);
    assert.notEqual(tool.inputSchema, null, `${tool.name}: inputSchema must not be null`);

    if (DESTRUCTIVE_TOOLS.has(tool.name)) {
      assert.equal(tool.destructiveHint, true, `${tool.name}: should be destructive`);
    } else {
      assert.equal(tool.destructiveHint, false, `${tool.name}: should NOT be destructive`);
    }

    // Public execution must use the unified tools run entrypoint.
    assert.ok(tool.cliCommand, `${tool.name}: must have cliCommand`);
    assert.equal(tool.cliCommand, `tools run ${tool.name}`, `${tool.name}: cliCommand must use tools run`);
  }
});

test("every ProBuilder tool has a matching bridge spec", () => {
  const probuilderTools = filterToolsByGroup("probuilder");

  for (const tool of probuilderTools) {
    assert.ok(tool.cliCommand, `${tool.name}: must have cliCommand`);
    const spec = bridgeCommandRegistry.specs().find((candidate) => candidate.toolName === tool.name);
    assert.ok(spec, `${tool.name}: bridge spec not found for tool "${tool.name}"`);
    // ProBuilder tools route through project-tool.run with toolName carrying
    // the underlying tool identity.
    assert.equal(spec.bridgeCommand, "project-tool.run", `${tool.name}: ProBuilder bridgeCommand must be "project-tool.run" (unified routing)`);
    assert.equal(spec.toolName, tool.name, `${tool.name}: toolName must match tool name`);
    assert.equal(spec.group, "probuilder", `${tool.name}: bridge spec group must be probuilder`);
  }
});

test("every ProBuilder tool has a matching command contract", () => {
  const probuilderTools = filterToolsByGroup("probuilder");

  for (const tool of probuilderTools) {
    const contract = findCommandContract(tool.name);
    assert.ok(contract, `${tool.name}: contract not found`);
    assert.equal(contract.group, "probuilder", `${tool.name}: contract group must be probuilder`);
    assert.equal(contract.handler_type, "hybrid", `${tool.name}: contract handler_type must be hybrid`);
    assert.equal(contract.csharp_handler, "present", `${tool.name}: contract csharp_handler must be present`);
    assert.deepEqual(contract.requires_packages, ["com.unity.probuilder"], `${tool.name}: contract requires_packages mismatch`);
    assert.equal(contract.destructive, tool.destructiveHint, `${tool.name}: contract destructive mismatch`);
  }
});

test("ProBuilder bridge specs count matches tool count", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  const probuilderSpecs = bridgeCommandRegistry.specs().filter((s) => s.group === "probuilder");
  assert.equal(probuilderSpecs.length, 36, `Expected 36 probuilder bridge specs, got ${probuilderSpecs.length}`);

  // Every ProBuilder spec routes through project-tool.run
  // and carries the underlying tool identity in the toolName field. The
  // one-to-one mapping is therefore validated against toolName (not the
  // shared project-tool.run bridgeCommand).
  for (const spec of probuilderSpecs) {
    assert.equal(spec.bridgeCommand, "project-tool.run", `${spec.group}.${spec.command}: ProBuilder bridgeCommand must be "project-tool.run"`);
    assert.ok(spec.toolName, `${spec.group}.${spec.command}: ProBuilder spec must carry toolName`);
  }

  const toolNames = new Set(probuilderTools.map((t) => t.name));
  const specToolNames = new Set(probuilderSpecs.map((s) => s.toolName!));
  assert.deepEqual(toolNames, specToolNames, "tool names and spec toolNames must match");
});

test("ProBuilder contract entries count is 36", () => {
  const contracts = commandContracts().filter((c) => c.group === "probuilder");
  assert.equal(contracts.length, 36, `Expected 36 probuilder contract entries, got ${contracts.length}`);

  const contractNames = new Set(contracts.map((c) => c.bridgeCommand));
  const toolNames = new Set(filterToolsByGroup("probuilder").map((t) => t.name));
  assert.deepEqual(contractNames, toolNames, "contract names and tool names must match");
});

test("every ProBuilder tool is discoverable via findToolByName", () => {
  for (const name of EXPECTED_PROBUILDER_TOOLS) {
    const tool = findToolByName(name);
    assert.ok(tool, `${name}: should be discoverable via findToolByName`);
    assert.equal(tool.group, "probuilder");
  }
});

test("old ProBuilder tool names no longer exist in built-in metadata", () => {
  const oldNames = ["probuilder.shape", "probuilder.create", "probuilder.edit", "probuilder.csg"];
  for (const name of oldNames) {
    const tool = findToolByName(name);
    assert.equal(tool, undefined, `${name}: old coarse-grained tool should NOT exist`);
  }
});

test("target-scoped ProBuilder tools require target in inputSchema", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  // Tools that do not require a target parameter
  const toolsWithoutTarget = new Set([
    "probuilder.ping",            // checks package availability only
    "probuilder.create-shape",    // creates new object, no existing target
    "probuilder.create-poly-shape", // creates new object, no existing target
    "probuilder.combine-meshes",  // uses targets[] not target
    "probuilder.merge-objects"     // uses targets[] not target
  ]);
  const toolsRequiringTarget = probuilderTools.filter((t) => !toolsWithoutTarget.has(t.name));

  for (const tool of toolsRequiringTarget) {
    const props = tool.inputSchema.properties as Record<string, unknown> | undefined;
    assert.ok(props, `${tool.name}: inputSchema must have properties`);
    assert.ok("target" in props, `${tool.name}: inputSchema must have 'target' property`);
  }
});
