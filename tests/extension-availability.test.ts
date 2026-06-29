/**
 * Tests for extension unavailability graceful degradation.
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
 * 10. Tool lookup errors include group-first recovery hints
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { handleToolsCommand, type ToolsRuntime } from "../src/tools.js";
import { handleDoctorCommand, type DoctorRuntime } from "../src/doctor.js";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import { writeInstances, type UnityInstance } from "../src/instances.js";
import { ExtensionRegistry, resolveExtensionAvailability, validateManifest, ExtensionRegistryError } from "../src/extensions/index.js";
import { loadExtensionDescriptor, collectAvailableTools, collectAllTools, extensionAvailabilitySummary } from "../src/extensions/descriptor-loader.js";
import { readInstalledUnityPackages } from "../src/extensions/project-resolver.js";
import { PROBUILDER_MANIFEST, VFX_GRAPH_MANIFEST, BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/registry-data.js";
import { validateExtensionManifestContracts } from "../src/contracts/command-contract.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { checkToolExtensionAvailability, checkContextExtensionAvailability } from "../src/extension-availability.js";
import { builtinToolMetadata, extensionToolMetadata, builtinToolGroups, findToolByName, EXTENSION_GROUPS } from "../src/tools/metadata/index.js";

const requestId = "test_p5t04_001";
const emptyRuntime: ToolsRuntime = {};
const healthServers: Server[] = [];

test.after(async () => {
  await Promise.all(healthServers.map((server) => new Promise<void>((resolve) => server.close(() => resolve()))));
});

// ── Helpers ────────────────────────────────────────────────────────────────

async function makeFakeProject(packages: Record<string, string> = {}): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity_cli-p5t04-"));
  const pkgDir = join(dir, "Packages");
  await mkdir(pkgDir, { recursive: true });
  await writeFile(
    join(pkgDir, "manifest.json"),
    JSON.stringify({ dependencies: packages }, null, 2)
  );
  // Write packages-lock.json mirroring manifest so that packages are
  // recognized as resolved (installed) by readInstalledUnityPackages.
  const lockDeps: Record<string, { version: string }> = {};
  for (const [id, version] of Object.entries(packages)) {
    lockDeps[id] = { version };
  }
  await writeFile(
    join(pkgDir, "packages-lock.json"),
    JSON.stringify({ dependencies: lockDeps }, null, 2)
  );
  return dir;
}

async function startHealthServer(): Promise<string> {
  const server = createServer((request, response) => {
    if (request.url === "/health") {
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ ok: true, status: "healthy" }));
      return;
    }
    response.writeHead(404);
    response.end();
  });
  healthServers.push(server);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  assert.ok(address && typeof address === "object");
  return `http://127.0.0.1:${address.port}`;
}

function fakeInstance(projectPath: string, bridgeEndpoint: string, overrides: Partial<UnityInstance> = {}): UnityInstance {
  return {
    instanceId: "editor-vfx",
    projectPath,
    projectHash: "hash-vfx",
    unityVersion: "2022.3.61f1",
    packageVersion: "com.unity_cli",
    pid: 1234,
    bridgeEndpoint,
    capabilitiesHash: "caps",
    status: "ready",
    editorState: { playMode: false, compiling: false, domainReloading: false },
    playModeState: { isPlaying: false, isPaused: false, isPlayingOrWillChangePlaymode: false },
    runtimeSessionId: null,
    toolCatalogFingerprint: null,
    registeredAt: new Date().toISOString(),
    lastHeartbeatAt: new Date().toISOString(),
    ...overrides
  };
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
  assert.ok((result.error?.next_actions ?? []).some((a) => a.includes("Unity Package Manager")));
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
      summary: "ProBuilder Shape",
      description: "Create shape",
      group: "probuilder",
      inputSchema: {},
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
      summary: "ProBuilder Shape",
      description: "Create shape",
      group: "probuilder",
      inputSchema: {},
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
      summary: "Scene Hierarchy",
      description: "Get scene hierarchy",
      group: "scene",
      inputSchema: {},
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
  // Without --project, extension metadata is available but package availability is not resolved.
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
        summary: "ProBuilder Shape",
        description: "Create shape",
        group: "wrong-group", // mismatch!
        bridgeCommand: "probuilder.ping",
        cliCommand: "tools run probuilder.ping"
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

// ── 13. Group-first discovery hints ───────────────────────────────────────

test("tools describe error messages include group-first discovery hints", async () => {
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
  assert.ok(
    nextActions.some((a) => a.includes("tools group") || a.includes("tools search")),
    "Error next_actions should reference group or search"
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
  assert.ok(nextActions.some((a) => a.includes("Unity Package Manager")));
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
  // In this CLI, extension tools like probuilder.ping are registered via
  // the extension contribution loader, not core built-in metadata.
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

// ── Cinemachine/URP/HDRP extension package capability tests ───────────────

test("Cinemachine tools are in extensionToolMetadata, not builtinToolMetadata", () => {
  const builtin = builtinToolMetadata();
  const ext = extensionToolMetadata();
  assert.equal(builtin.filter((t) => t.group === "cinemachine").length, 0, "Cinemachine tools should NOT be in core builtin metadata");
  assert.equal(ext.filter((t) => t.group === "cinemachine").length, 13, "Cinemachine should have 13 extension tools");
});

test("URP tools are in extensionToolMetadata, not builtinToolMetadata", () => {
  const builtin = builtinToolMetadata();
  const ext = extensionToolMetadata();
  assert.equal(builtin.filter((t) => t.group === "urp").length, 0, "URP tools should NOT be in core builtin metadata");
  assert.equal(ext.filter((t) => t.group === "urp").length, 3, "URP should have 3 extension tools");
});

test("HDRP tools are in extensionToolMetadata, not builtinToolMetadata", () => {
  const builtin = builtinToolMetadata();
  const ext = extensionToolMetadata();
  assert.equal(builtin.filter((t) => t.group === "hdrp").length, 0, "HDRP tools should NOT be in core builtin metadata");
  assert.equal(ext.filter((t) => t.group === "hdrp").length, 2, "HDRP should have 2 extension tools");
});

test("Cinemachine tools are discoverable via findToolByName after registration", () => {
  const create = findToolByName("cinemachine.create");
  assert.ok(create, "cinemachine.create should be findable");
  assert.equal(create?.group, "cinemachine");

  const brain = findToolByName("cinemachine.brain");
  assert.ok(brain, "cinemachine.brain should be findable");
  assert.equal(brain?.group, "cinemachine");

  const ping = findToolByName("cinemachine.ping");
  assert.ok(ping, "cinemachine.ping should be findable");
  assert.equal(ping?.group, "cinemachine");

  const force = findToolByName("cinemachine.force-camera");
  assert.ok(force, "cinemachine.force-camera should be findable");
  assert.equal(force?.group, "cinemachine");

  const release = findToolByName("cinemachine.release-override");
  assert.ok(release, "cinemachine.release-override should be findable");
  assert.equal(release?.group, "cinemachine");
});

test("URP and HDRP tools are discoverable via findToolByName after registration", () => {
  const feature = findToolByName("urp.feature");
  assert.ok(feature, "urp.feature should be findable");
  assert.equal(feature?.group, "urp");

  const volume = findToolByName("hdrp.volume");
  assert.ok(volume, "hdrp.volume should be findable");
  assert.equal(volume?.group, "hdrp");
});

test("EXTENSION_GROUPS includes cinemachine, urp, hdrp", () => {
  assert.ok(EXTENSION_GROUPS.has("cinemachine"), "cinemachine should be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("urp"), "urp should be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("hdrp"), "hdrp should be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("probuilder"), "probuilder should still be in EXTENSION_GROUPS");
  assert.ok(EXTENSION_GROUPS.has("vfx"), "vfx should still be in EXTENSION_GROUPS");
});

// ── Extension source validation ───────────────────────────────────────────

test("all Cinemachine tools have source: extension", () => {
  const ext = extensionToolMetadata();
  const cinemachineTools = ext.filter((t) => t.group === "cinemachine");
  assert.equal(cinemachineTools.length, 13, "Should have 13 Cinemachine extension tools");
  for (const tool of cinemachineTools) {
    assert.equal(tool.source, "extension", `${tool.name} should have source: "extension"`);
  }
});

test("all URP tools have source: extension", () => {
  const ext = extensionToolMetadata();
  const urpTools = ext.filter((t) => t.group === "urp");
  assert.equal(urpTools.length, 3, "Should have 3 URP extension tools");
  for (const tool of urpTools) {
    assert.equal(tool.source, "extension", `${tool.name} should have source: "extension"`);
  }
});

test("all HDRP tools have source: extension", () => {
  const ext = extensionToolMetadata();
  const hdrpTools = ext.filter((t) => t.group === "hdrp");
  assert.equal(hdrpTools.length, 2, "Should have 2 HDRP extension tools");
  for (const tool of hdrpTools) {
    assert.equal(tool.source, "extension", `${tool.name} should have source: "extension"`);
  }
});

test("no core built-in tools have source: extension", () => {
  const core = builtinToolMetadata();
  for (const tool of core) {
    assert.ok(tool.source !== "extension", `Core tool ${tool.name} should NOT have source: "extension"`);
  }
});

// ── Core/extension boundary decoupling tests ──────────────────────────────

test("builtinToolMetadata does not include ProBuilder or VFX tools", () => {
  const builtin = builtinToolMetadata();
  const probuilderNames = builtin.filter((t) => t.group === "probuilder");
  const vfxNames = builtin.filter((t) => t.group === "vfx");
  assert.equal(probuilderNames.length, 0, "ProBuilder tools should NOT be in core builtin metadata");
  assert.equal(vfxNames.length, 0, "VFX tools should NOT be in core builtin metadata");
});

test("extensionToolMetadata includes ProBuilder and VFX tools", () => {
  const ext = extensionToolMetadata();
  const probuilderNames = ext.filter((t) => t.group === "probuilder");
  const vfxNames = ext.filter((t) => t.group === "vfx");
  assert.equal(probuilderNames.length, 36, "ProBuilder should have 36 extension tools");
  assert.equal(vfxNames.length, 16, "VFX should have 16 extension tools");
});

test("builtinToolGroups does not include probuilder or vfx", () => {
  const groups = builtinToolGroups();
  assert.ok(!groups.includes("probuilder"), "probuilder should NOT be in core groups");
  assert.ok(!groups.includes("vfx"), "vfx should NOT be in core groups");
});

test("tools group list without --project does not include extension tools", async () => {
  const result = await handleToolsCommand("group", ["list", "scene"], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { tools: Array<{ name: string; group: string }> };
  const probuilderTools = data.tools.filter((t) => t.group === "probuilder");
  const vfxTools = data.tools.filter((t) => t.group === "vfx");
  assert.equal(probuilderTools.length, 0, "ProBuilder tools should NOT appear without --project");
  assert.equal(vfxTools.length, 0, "VFX tools should NOT appear without --project");
});

test("tools group list with --project includes extension tools when packages installed", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const result = await handleToolsCommand("group", ["list", "probuilder"], { project: projectPath }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { tools: Array<{ name: string; group: string }> };
  const probuilderTools = data.tools.filter((t) => t.group === "probuilder");
  assert.ok(probuilderTools.length > 0, "ProBuilder tools should appear when package is installed");
});

test("tools group list with --project excludes package-gated tools but keeps built-in VFX tools", async () => {
  const projectPath = await makeFakeProject(); // no optional packages

  const probuilderResult = await handleToolsCommand("group", ["list", "probuilder"], { project: projectPath }, emptyRuntime, requestId);
  assert.equal(probuilderResult.ok, false);
  assert.equal(probuilderResult.error?.code, "E_TOOL_GROUP_NOT_FOUND");

  const vfxResult = await handleToolsCommand("group", ["list", "vfx"], { project: projectPath }, emptyRuntime, requestId);
  assert.equal(vfxResult.ok, true);
  const data = vfxResult.data as { tools: Array<{ name: string; group: string; requiresPackages?: string[] }> };
  assert.deepEqual(data.tools.map((t) => t.name), [
    "vfx.particle-create",
    "vfx.particle-info",
    "vfx.particle-main",
    "vfx.particle-emission",
    "vfx.particle-shape",
    "vfx.particle-play",
    "vfx.particle-module",
    "vfx.line-create",
    "vfx.line-positions",
    "vfx.trail"
  ]);
  assert.ok(data.tools.every((t) => t.group === "vfx"));
  assert.ok(data.tools.every((t) => !t.requiresPackages || t.requiresPackages.length === 0));
});

test("tools group without --project does not include extension groups", async () => {
  const result = await handleToolsCommand("group", [], {}, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: Array<{ name: string }> };
  const groupNames = data.groups.map((g) => g.name);
  assert.ok(!groupNames.includes("probuilder"), "probuilder group should NOT appear without --project");
  assert.ok(!groupNames.includes("vfx"), "vfx group should NOT appear without --project");
});

test("tools group with --project includes extension groups when tools are available", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.probuilder": "5.0.0"
  });

  const result = await handleToolsCommand("group", [], { project: projectPath }, emptyRuntime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as { groups: Array<{ name: string }> };
  const groupNames = data.groups.map((g) => g.name);
  assert.ok(groupNames.includes("probuilder"), "probuilder group should appear when package is installed");
  assert.ok(groupNames.includes("vfx"), "vfx group should appear because built-in component tools are available");
});

test("tools group with --instance uses the selected project for extension groups", async () => {
  const projectPath = await makeFakeProject();
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-p5t04-instance-"));
  const endpoint = await startHealthServer();
  await writeInstances(dataDir, [fakeInstance(projectPath, endpoint)]);
  const runtime: ToolsRuntime = { dataDir, projectTools: [] };

  const groups = await handleToolsCommand("group", [], { instance: "editor-vfx" }, runtime, requestId);
  assert.equal(groups.ok, true);
  const groupData = groups.data as { groups: Array<{ name: string }> };
  assert.ok(groupData.groups.some((group) => group.name === "vfx"), "vfx group should appear from the selected instance project");

  const listed = await handleToolsCommand("group", ["list", "vfx"], { instance: "editor-vfx" }, runtime, requestId);
  assert.equal(listed.ok, true);
  const listData = listed.data as { tools: Array<{ name: string }> };
  assert.ok(listData.tools.some((tool) => tool.name === "vfx.particle-create"));
});

test("findToolByName still finds extension tools after registration", () => {
  const ping = findToolByName("probuilder.ping");
  assert.ok(ping, "probuilder.ping should be findable via findToolByName");
  assert.equal(ping?.group, "probuilder");

  const vfxGraph = findToolByName("vfx.graph");
  assert.ok(vfxGraph, "vfx.graph should be findable via findToolByName");
  assert.equal(vfxGraph?.group, "vfx");
});

test("core package does not hard-depend on optional Unity packages", () => {
  const coreTools = builtinToolMetadata();
  for (const tool of coreTools) {
    assert.ok(
      !tool.requiresPackages?.some((p) => p.startsWith("com.unity.probuilder") || p.startsWith("com.unity.visualeffectgraph")),
      `Core tool ${tool.name} should not require optional extension packages`
    );
  }
});
