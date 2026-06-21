/**
 * Contract/snapshot tests for ProBuilder extension — 36 single-action tools.
 *
 * Validates:
 * - 36 ProBuilder tool names, schemas, requiresPackages, readOnly/destructive,
 *   target, cliCommand and bridge command consistency
 * - tools group list probuilder discovers 36 tools
 * - tools describe probuilder.* works for each tool
 * - Core capabilities no longer lists ProBuilder as a built-in C# domain
 *
 * Source: P7-T1 alignment matrix (ALP-596), P7-T2a task scope (ALP-612)
 */

import assert from "node:assert/strict";
import test from "node:test";
import { builtinToolMetadata, allRegisteredToolMetadata, filterToolsByGroup, findToolByName, extensionToolMetadata, EXTENSION_GROUPS, type ToolMetadata } from "./tools/metadata/index.js";
import { bridgeCommandRegistry } from "./bridge/index.js";
import { commandContracts, findCommandContract } from "./contracts/command-contract.js";
// P10-R3: Ensure extension contributions (ProBuilder, Cinemachine, VFX, URP, HDRP) are registered
import "./extensions/index.js";

// ─── Expected 36 tool names per P7-T1 matrix ────────────────────────────

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

// Read-only tools per P7-T1 matrix
const READ_ONLY_TOOLS = new Set([
  "probuilder.ping",
  "probuilder.select-faces",
  "probuilder.mesh-info",
  "probuilder.validate-mesh"
]);

// Destructive tools per P7-T1 matrix
const DESTRUCTIVE_TOOLS = new Set([
  "probuilder.delete-faces",
  "probuilder.combine-meshes",
  "probuilder.merge-objects"
]);

test("ProBuilder extension has exactly 36 tools", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  assert.equal(probuilderTools.length, 36, `Expected 36 probuilder tools, got ${probuilderTools.length}`);
});

test("ProBuilder tool names match P7-T1 alignment matrix exactly", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  const actualNames = probuilderTools.map((t) => t.name).sort();
  const expectedNames = [...EXPECTED_PROBUILDER_TOOLS].sort();
  assert.deepEqual(actualNames, expectedNames);
});

test("every ProBuilder tool has consistent metadata", () => {
  const probuilderTools = filterToolsByGroup("probuilder");

  for (const tool of probuilderTools) {
    // Unified constraints per P7-T1
    assert.equal(tool.group, "probuilder", `${tool.name}: group must be probuilder`);
    assert.equal(tool.target, "editor", `${tool.name}: target must be editor`);
    assert.equal(tool.availabilityScope, "editorSession", `${tool.name}: availabilityScope must be editorSession`);
    assert.deepEqual(tool.requiresPackages, ["com.unity.probuilder"], `${tool.name}: requiresPackages must be ["com.unity.probuilder"]`);
    assert.equal(tool.handler, "hybrid", `${tool.name}: handler must be hybrid`);
    assert.equal(tool.docsTopic, "probuilder", `${tool.name}: docsTopic must be probuilder`);

    // inputSchema must be an object
    assert.equal(typeof tool.inputSchema, "object", `${tool.name}: inputSchema must be an object`);
    assert.notEqual(tool.inputSchema, null, `${tool.name}: inputSchema must not be null`);

    // readOnly/destructive consistency
    if (READ_ONLY_TOOLS.has(tool.name)) {
      assert.equal(tool.readOnlyHint, true, `${tool.name}: should be readOnly`);
    } else {
      assert.equal(tool.readOnlyHint, false, `${tool.name}: should NOT be readOnly`);
    }

    if (DESTRUCTIVE_TOOLS.has(tool.name)) {
      assert.equal(tool.destructiveHint, true, `${tool.name}: should be destructive`);
    } else {
      assert.equal(tool.destructiveHint, false, `${tool.name}: should NOT be destructive`);
    }

    // Must not be both readOnly and destructive
    assert.equal(tool.readOnlyHint && tool.destructiveHint, false, `${tool.name}: cannot be both readOnly and destructive`);

    // Must have a cliCommand
    assert.ok(tool.cliCommand, `${tool.name}: must have cliCommand`);
    assert.ok(tool.cliCommand.startsWith("probuilder "), `${tool.name}: cliCommand must start with "probuilder "`);
  }
});

test("every ProBuilder tool has a matching bridge spec", () => {
  const probuilderTools = filterToolsByGroup("probuilder");

  for (const tool of probuilderTools) {
    assert.ok(tool.cliCommand, `${tool.name}: must have cliCommand`);
    const [group, ...commandParts] = tool.cliCommand.split(" ");
    const command = commandParts.join(" ");
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.ok(spec, `${tool.name}: bridge spec not found for cliCommand "${tool.cliCommand}"`);
    // ALP-664 / ALP-505: ProBuilder tools route through the unified
    // project-tool.run bridge command with toolName carrying the underlying
    // tool identity. The legacy probuilder.* direct bridge commands were
    // retired because they produced E_BRIDGE_COMMAND_UNSUPPORTED /
    // E_TOOL_NOT_FOUND on the real Unity bridge.
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
    assert.equal(contract.csharp_handler, "present", `${tool.name}: contract csharp_handler must be present (P7-T2b delivered)`);
    assert.deepEqual(contract.requires_packages, ["com.unity.probuilder"], `${tool.name}: contract requires_packages mismatch`);
    assert.equal(contract.readOnly, tool.readOnlyHint, `${tool.name}: contract readOnly mismatch`);
    assert.equal(contract.destructive, tool.destructiveHint, `${tool.name}: contract destructive mismatch`);
  }
});

test("ProBuilder bridge specs count matches tool count", () => {
  const probuilderTools = filterToolsByGroup("probuilder");
  const probuilderSpecs = bridgeCommandRegistry.specs().filter((s) => s.group === "probuilder");
  assert.equal(probuilderSpecs.length, 36, `Expected 36 probuilder bridge specs, got ${probuilderSpecs.length}`);

  // ALP-664 / ALP-505: Every ProBuilder spec routes through project-tool.run
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
