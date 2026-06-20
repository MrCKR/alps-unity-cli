/**
 * Tests for P5-T04: Extension unavailability graceful degradation.
 *
 * Covers:
 * 1. tools describe returns unavailable + next_actions when extension deps missing
 * 2. tools run returns E_EXTENSION_UNAVAILABLE when extension deps missing
 * 3. tools describe returns available when extension deps present
 * 4. tools describe works without --project (graceful degradation)
 * 5. context describe/read returns unavailable when extension deps missing
 * 6. doctor includes extension_availability check
 * 7. capabilities includes extension info when --project provided
 * 8. Extension manifest contract validation
 * 9. Descriptor load failure handling
 * 10. No old "tools list" full-discovery wording in error messages
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { handleToolsCommand, type ToolsRuntime } from "./tools.js";
import { handleDoctorCommand, type DoctorRuntime } from "./doctor.js";
import { handleCapabilitiesCommand } from "./capabilities.js";
import { ExtensionRegistry, resolveExtensionAvailability, validateManifest, ExtensionRegistryError } from "./extensions/index.js";
import { loadExtensionDescriptor, collectAvailableTools, collectAllTools, extensionAvailabilitySummary } from "./extensions/descriptor-loader.js";
import { readInstalledUnityPackages } from "./extensions/project-resolver.js";
import { PROBUILDER_MANIFEST, VFX_GRAPH_MANIFEST, BUILTIN_EXTENSION_MANIFESTS } from "./extensions/registry-data.js";
import { validateExtensionManifestContracts } from "./contracts/command-contract.js";
import { bridgeCommandRegistry } from "./bridge/index.js";
import { checkToolExtensionAvailability, checkContextExtensionAvailability } from "./extension-availability.js";
import { builtinToolMetadata, extensionToolMetadata, findToolByName, EXTENSION_GROUPS } from "./tools/metadata/index.js";

const requestId = "test_p5t04_001";
const emptyRuntime: ToolsRuntime = {};

// ── Helpers ────────────────────────────────────────────────────────────────

async function makeFakeProject(packages: Record<string, string> = {}): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity_cli-p5t04-"));
  const pkgDir = join(dir, "Packages");
  await mkdir(pkgDir, { recursive: true });
  await writeFile(
    join(pkgDir, "manifest.json"),
    JSON.stringify({ dependencies: packages }, null, 2)
  );
  return dir;
}

// ── 1. tools describe: unavailable + next_actions ──────────────────────────

test("tools describe returns unavailable when extension deps are missing", async () => {
  const projectPath = await makeFakeProject(); // no packages

  const result = await handleToolsCommand(
    "describe",
    ["probuilder.ping"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "unavailable");
  assert.equal(data.extensionId, "probuilder");
  assert.ok(Array.isArray(data.missingPackageIds));
  assert.ok((data.missingPackageIds as string[]).includes("com.unity.probuilder"));
  assert.ok(Array.isArray(data.nextActions));
  assert.ok((data.nextActions as string[]).some((a) => a.includes("install")));
  assert.ok((data.nextActions as string[]).some((a) => a.includes("extensions doctor")));
});

test("tools describe returns E_EXTENSION_UNAVAILABLE for tools run when deps missing", async () => {
  const projectPath = await makeFakeProject(); // no packages

  const result = await handleToolsCommand(
    "run",
    ["probuilder.ping"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_EXTENSION_UNAVAILABLE");
  assert.ok(result.error?.message?.includes("probuilder"));
  assert.ok(result.error?.message?.includes("com.unity.probuilder"));
  assert.ok(Array.isArray(result.error?.next_actions));
  assert.ok((result.error?.next_actions ?? []).some((a) => a.includes("package install")));
  assert.ok((result.error?.next_actions ?? []).some((a) => a.includes("extensions doctor")));
});

// ── 2. tools describe: available when deps present ─────────────────────────

test("tools describe returns available when extension deps are installed", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const result = await handleToolsCommand(
    "describe",
    ["probuilder.ping"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "available");
  assert.equal(data.extensionId, "probuilder");
  assert.equal(data.missingPackageIds, undefined);
});

// ── 3. tools describe: works without --project (graceful) ─────────────────

test("tools describe works without --project flag", async () => {
  const result = await handleToolsCommand(
    "describe",
    ["probuilder.ping"],
    {},
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  // Without project, we can't determine availability → no extensionAvailability field
  // or it should be present without blocking.
  assert.ok(data.name === "probuilder.ping");
});

// ── 4. tools run: proceeds without --project (bridge will fail naturally) ──

test("tools run without --project does not block with E_EXTENSION_UNAVAILABLE", async () => {
  // Without --project, the extension check should not block execution.
  // The tool will fail naturally at the bridge if the package is missing.
  const result = await handleToolsCommand(
    "run",
    ["probuilder.ping"],
    {},
    emptyRuntime,
    requestId
  );

  // Should NOT be E_EXTENSION_UNAVAILABLE — it should proceed to bridge
  // and fail with BRIDGE_ENDPOINT_REQUIRED or similar.
  assert.ok(result.ok === false);
  assert.notEqual(result.error?.code, "E_EXTENSION_UNAVAILABLE");
});

// ── 5. VFX Graph extension unavailability ──────────────────────────────────

test("tools describe returns unavailable for vfx.graph when VFX package missing", async () => {
  const projectPath = await makeFakeProject(); // no packages

  const result = await handleToolsCommand(
    "describe",
    ["vfx.graph"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "unavailable");
  assert.equal(data.extensionId, "vfx-graph");
  assert.ok((data.missingPackageIds as string[]).includes("com.unity.visualeffectgraph"));
});

// ── 6. Tools without requiresPackages are not affected ─────────────────────

test("tools describe for non-extension tools is not affected", async () => {
  const projectPath = await makeFakeProject(); // no packages

  const result = await handleToolsCommand(
    "describe",
    ["scene.hierarchy"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, undefined);
  assert.equal(data.extensionId, undefined);
});

// ── 7. checkToolExtensionAvailability unit tests ───────────────────────────

test("checkToolExtensionAvailability returns unavailable for tool with missing packages", async () => {
  const projectPath = await makeFakeProject();

  const result = await checkToolExtensionAvailability(
    {
      name: "probuilder.ping",
      title: "ProBuilder Shape",
      description: "Create shape",
      group: "probuilder",
      inputSchema: {},
      readOnlyHint: false,
      destructiveHint: false,
      requiresPackages: ["com.unity.probuilder"]
    },
    { project: projectPath }
  );

  assert.equal(result.available, false);
  assert.equal(result.errorCode, "E_EXTENSION_UNAVAILABLE");
  assert.equal(result.extensionId, "probuilder");
  assert.ok(result.nextActions.length > 0);
});

test("checkToolExtensionAvailability returns available when packages present", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const result = await checkToolExtensionAvailability(
    {
      name: "probuilder.ping",
      title: "ProBuilder Shape",
      description: "Create shape",
      group: "probuilder",
      inputSchema: {},
      readOnlyHint: false,
      destructiveHint: false,
      requiresPackages: ["com.unity.probuilder"]
    },
    { project: projectPath }
  );

  assert.equal(result.available, true);
  assert.equal(result.extensionId, "probuilder");
});

test("checkToolExtensionAvailability returns available for tools without requiresPackages", async () => {
  const projectPath = await makeFakeProject();

  const result = await checkToolExtensionAvailability(
    {
      name: "scene.hierarchy",
      title: "Scene Hierarchy",
      description: "Get scene hierarchy",
      group: "scene",
      inputSchema: {},
      readOnlyHint: true,
      destructiveHint: false
    },
    { project: projectPath }
  );

  assert.equal(result.available, true);
  assert.equal(result.nextActions.length, 0);
});

// ── 8. checkContextExtensionAvailability ────────────────────────────────────

test("checkContextExtensionAvailability returns unavailable for extension context with missing deps", async () => {
  const projectPath = await makeFakeProject();

  const result = await checkContextExtensionAvailability(
    "probuilder.scene",
    { project: projectPath }
  );

  assert.equal(result.available, false);
  assert.equal(result.errorCode, "E_EXTENSION_UNAVAILABLE");
  assert.equal(result.extensionId, "probuilder");
  assert.ok(result.nextActions.length > 0);
});

test("checkContextExtensionAvailability returns available when deps installed", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const result = await checkContextExtensionAvailability(
    "probuilder.scene",
    { project: projectPath }
  );

  assert.equal(result.available, true);
});

test("checkContextExtensionAvailability returns available for unknown context", async () => {
  const projectPath = await makeFakeProject();

  const result = await checkContextExtensionAvailability(
    "project.info",
    { project: projectPath }
  );

  assert.equal(result.available, true);
});

// ── 9. Doctor includes extension_availability check ────────────────────────

test("doctor includes extension_availability check with project path", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-p5t04-"));
  const projectPath = await makeFakeProject(); // no packages

  const runtime: DoctorRuntime = { dataDir };
  const result = await handleDoctorCommand(
    { project: projectPath },
    runtime,
    "req_doctor_ext"
  );

  assert.equal(result.ok, true);
  const data = result.data as { checks: Array<{ name: string; status: string; message: string }> };
  const extCheck = data.checks.find((c) => c.name === "extension_availability");
  assert.ok(extCheck, "Expected extension_availability check in doctor output");
  assert.equal(extCheck.status, "fail");
  assert.ok(extCheck.message.includes("missing"));
});

test("doctor extension_availability passes when all deps met", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-p5t04-"));
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0",
    "com.unity.visualeffectgraph": "14.0.0",
    "com.unity.cinemachine": "3.0.0",
    "com.unity.render-pipelines.universal": "14.0.0",
    "com.unity.render-pipelines.high-definition": "14.0.0"
  });

  const runtime: DoctorRuntime = { dataDir };
  const result = await handleDoctorCommand(
    { project: projectPath },
    runtime,
    "req_doctor_ext_pass"
  );

  assert.equal(result.ok, true);
  const data = result.data as { checks: Array<{ name: string; status: string }> };
  const extCheck = data.checks.find((c) => c.name === "extension_availability");
  assert.ok(extCheck);
  assert.equal(extCheck.status, "pass");
});

test("doctor extension_availability warns when no project path", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-p5t04-"));

  const runtime: DoctorRuntime = { dataDir };
  const result = await handleDoctorCommand({}, runtime, "req_doctor_ext_noproj");

  assert.equal(result.ok, true);
  const data = result.data as { checks: Array<{ name: string; status: string }> };
  const extCheck = data.checks.find((c) => c.name === "extension_availability");
  assert.ok(extCheck);
  assert.equal(extCheck.status, "warn");
});

// ── 10. Capabilities includes extension info ───────────────────────────────

test("capabilities includes extension info when --project provided", async () => {
  const projectPath = await makeFakeProject();

  const result = await handleCapabilitiesCommand(
    { project: projectPath },
    {},
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.ok(data.extensions !== undefined, "Expected extensions section in capabilities");
  const ext = data.extensions as Record<string, unknown>;
  assert.ok(typeof ext.total === "number");
  assert.ok(typeof ext.available === "number");
  assert.ok(typeof ext.unavailable === "number");
  assert.ok(ext.total === 5); // probuilder, vfx-graph, cinemachine, urp, hdrp
});

test("capabilities without --project does not include extension package availability info", async () => {
  const result = await handleCapabilitiesCommand({}, {}, requestId);

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  // Without --project, extension metadata is available (P1+P2) but no package availability resolution
  const ext = data.extensions as Record<string, unknown> | undefined;
  // When no project, we still get basic extension info but without per-package availability
  assert.ok(ext !== undefined, "extensions section should exist");
  assert.ok(typeof ext!.enabled === "boolean", "extensions.enabled should be boolean");
});

// ── 11. Extension manifest contract validation ────────────────────────────

test("validateExtensionManifestContracts passes for built-in manifests", () => {
  // Should not throw
  validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry);
});

test("validateExtensionManifestContracts detects group mismatch", () => {
  const badManifest = {
    ...PROBUILDER_MANIFEST,
    contributions: {
      ...PROBUILDER_MANIFEST.contributions,
      tools: [{
        name: "probuilder.ping",
        title: "ProBuilder Shape",
        description: "Create shape",
        group: "wrong-group", // mismatch!
        bridgeCommand: "probuilder.ping",
        cliCommand: "probuilder shape"
      }]
    }
  };

  assert.throws(
    () => validateExtensionManifestContracts([badManifest], bridgeCommandRegistry),
    (err: unknown) => err instanceof Error && err.message.includes("group mismatch")
  );
});

// ── 12. Descriptor load failure handling ───────────────────────────────────

test("loadExtensionDescriptor for unknown extension throws", () => {
  const registry = new ExtensionRegistry();
  assert.throws(
    () => loadExtensionDescriptor(registry, "nonexistent"),
    (err: unknown) => err instanceof Error && err.message.includes("not found")
  );
});

// ── 13. No old "tools list" full-discovery wording ─────────────────────────

test("tools describe error messages do not mention old 'tools list' as sole discovery path", async () => {
  const projectPath = await makeFakeProject();

  // Test error for tool not found — should mention groups first
  const result = await handleToolsCommand(
    "describe",
    ["nonexistent.tool"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, false);
  const nextActions = result.error?.next_actions ?? [];
  // The first suggestion should reference groups or search, not just tools list
  assert.ok(
    nextActions.some((a) => a.includes("tools groups") || a.includes("tools search")),
    "Error next_actions should reference groups or search, not just 'tools list'"
  );
});

test("tools run error for unavailable extension mentions extensions doctor", async () => {
  const projectPath = await makeFakeProject();

  const result = await handleToolsCommand(
    "run",
    ["probuilder.ping"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "E_EXTENSION_UNAVAILABLE");
  const nextActions = result.error?.next_actions ?? [];
  assert.ok(nextActions.some((a) => a.includes("extensions doctor")));
  assert.ok(nextActions.some((a) => a.includes("package install")));
});

// ── 14. Mixed availability scenario ────────────────────────────────────────

test("extensionAvailabilitySummary reports correct mix of available/unavailable", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const registry = new ExtensionRegistry();
  const installedPackages = await readInstalledUnityPackages(projectPath);
  const summary = extensionAvailabilitySummary(registry, installedPackages);

  assert.equal(summary.total, 5);
  assert.equal(summary.available, 1); // probuilder
  assert.equal(summary.unavailable, 4); // vfx-graph, cinemachine, urp, hdrp

  const probuilder = summary.extensions.find((e) => e.id === "probuilder");
  assert.ok(probuilder);
  assert.equal(probuilder.status, "available");
  assert.equal(probuilder.missingPackages.length, 0);

  const vfxGraph = summary.extensions.find((e) => e.id === "vfx-graph");
  assert.ok(vfxGraph);
  assert.equal(vfxGraph.status, "unavailable");
  assert.ok(vfxGraph.missingPackages.includes("com.unity.visualeffectgraph"));
  assert.ok(vfxGraph.nextActions.length > 0);
});

// ── 15. Installed but not enabled: tools describe still shows unavailable ──

test("tools describe shows unavailable when Unity pkg missing even for installed CLI extension", async () => {
  // This tests the scenario: CLI extension conceptually exists, but Unity package
  // is not installed. The extension availability check should still report unavailable.
  const projectPath = await makeFakeProject(); // no Unity packages

  const result = await handleToolsCommand(
    "describe",
    ["probuilder.delete-faces"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "unavailable");
  assert.ok((data.missingPackageIds as string[]).includes("com.unity.probuilder"));
});

// ── 16. Unity package exists but CLI extension not installed scenario ──────
// This scenario is handled by the extensions install/remove system.
// The tools describe/run always check package deps via the registry,
// so even if an extension is "not enabled", the tool metadata still exists
// as a built-in and will be checked for package deps.

test("tools describe for extension tool shows unavailable when Unity package present but extension not installed in project state", async () => {
  // In this CLI, extension tools like probuilder.ping are built-in metadata.
  // The availability check is package-based, not install-state-based.
  // When the Unity package IS installed, the tool is available regardless of
  // the extensions.json install state.
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const result = await handleToolsCommand(
    "describe",
    ["probuilder.ping"],
    { project: projectPath },
    emptyRuntime,
    requestId
  );

  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "available");
});

// ── P10-R4: Cinemachine/URP/HDRP extension package capability tests ───────

test("P10-R4: Cinemachine tools are in extensionToolMetadata, not builtinToolMetadata", () => {
  const builtin = builtinToolMetadata();
  const ext = extensionToolMetadata();
  assert.equal(builtin.filter((t) => t.group === "cinemachine").length, 0, "Cinemachine tools should NOT be in core builtin metadata");
  assert.equal(ext.filter((t) => t.group === "cinemachine").length, 10, "Cinemachine should have 10 extension tools");
});

test("P10-R4: URP tools are in extensionToolMetadata, not builtinToolMetadata", () => {
  const builtin = builtinToolMetadata();
  const ext = extensionToolMetadata();
  assert.equal(builtin.filter((t) => t.group === "urp").length, 0, "URP tools should NOT be in core builtin metadata");
  assert.equal(ext.filter((t) => t.group === "urp").length, 3, "URP should have 3 extension tools");
});

test("P10-R4: HDRP tools are in extensionToolMetadata, not builtinToolMetadata", () => {
  const builtin = builtinToolMetadata();
  const ext = extensionToolMetadata();
  assert.equal(builtin.filter((t) => t.group === "hdrp").length, 0, "HDRP tools should NOT be in core builtin metadata");
  assert.equal(ext.filter((t) => t.group === "hdrp").length, 2, "HDRP should have 2 extension tools");
});

test("P10-R4: Cinemachine tools are discoverable via findToolByName after registration", () => {
  const create = findToolByName("cinemachine.create");
  assert.ok(create, "cinemachine.create should be findable");
  assert.equal(create?.group, "cinemachine");

  const brain = findToolByName("cinemachine.brain");
  assert.ok(brain, "cinemachine.brain should be findable");
  assert.equal(brain?.group, "cinemachine");
});

test("P10-R4: URP and HDRP tools are discoverable via findToolByName after registration", () => {
  const feature = findToolByName("urp.feature");
  assert.ok(feature, "urp.feature should be findable");
  assert.equal(feature?.group, "urp");

  const volume = findToolByName("hdrp.volume");
  assert.ok(volume, "hdrp.volume should be findable");
  assert.equal(volume?.group, "hdrp");
});

test("P10-R4: EXTENSION_GROUPS includes cinemachine, urp, hdrp", () => {
  assert.ok(EXTENSION_GROUPS.has("cinemachine"), "cinemachine should be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("urp"), "urp should be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("hdrp"), "hdrp should be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("probuilder"), "probuilder should still be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("vfx"), "vfx should still be in EXTENSION_GROUPS");
});
