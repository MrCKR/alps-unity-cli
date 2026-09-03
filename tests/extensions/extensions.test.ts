/**
 * Tests for the extension system: registry, descriptor loader, project resolver.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { cp, mkdtemp, readFile, writeFile, mkdir, rm, stat } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import {
  ExtensionRegistry,
  ExtensionRegistryError,
  validateManifest,
  resolveExtensionAvailability
} from "../../src/extensions/registry.js";

import {
  PROBUILDER_MANIFEST,
  VFX_GRAPH_MANIFEST,
  CINEMACHINE_MANIFEST,
  URP_MANIFEST,
  HDRP_MANIFEST,
  BUILTIN_EXTENSION_MANIFESTS
} from "../../src/extensions/index.js";

import {
  loadAllExtensionDescriptors,
  loadExtensionDescriptor,
  loadAvailableDescriptors,
  loadUnavailableDescriptors,
  collectAvailableTools,
  collectAllTools,
  collectAvailableCSharpEntries,
  extensionAvailabilitySummary
} from "../../src/extensions/descriptor-loader.js";

import {
  readInstalledUnityPackages,
  resolveExtensionAvailabilityForProject
} from "../../src/extensions/project-resolver.js";

import {
  checkExtensionBundles,
  extensionInstallRoot,
  extensionSourceRoot,
  installExtensionBundle,
  readExtensionInstallManifest,
  setAfterProjectionCopyForTest,
  setExtensionSourceRootForTest
} from "../../src/extensions/installer.js";

// ── Registry: built-in data ────────────────────────────────────────────────

test("registry lists five built-in extensions", () => {
  const registry = new ExtensionRegistry();
  assert.deepEqual(registry.extensionIds(), ["cinemachine", "hdrp", "probuilder", "urp", "vfx-graph"]);
  assert.equal(registry.listExtensions().length, 5);
});

test("registry has all five extension manifests", () => {
  const registry = new ExtensionRegistry();
  assert.ok(registry.hasExtension("probuilder"));
  assert.ok(registry.hasExtension("vfx-graph"));
  assert.ok(registry.hasExtension("cinemachine"));
  assert.ok(registry.hasExtension("urp"));
  assert.ok(registry.hasExtension("hdrp"));
  assert.ok(!registry.hasExtension("nonexistent"));
});

test("registry getExtension returns manifest for built-in extension", () => {
  const registry = new ExtensionRegistry();
  const ext = registry.getExtension("probuilder");
  assert.ok(ext);
  assert.equal(ext.manifest.id, "probuilder");
  assert.equal(ext.manifest.name, "ProBuilder");
  assert.equal(ext.manifest.unityPackages.length, 1);
  assert.equal(ext.manifest.unityPackages[0].packageId, "com.unity.probuilder");
  assert.equal(ext.manifest.unityPackages[0].versionRange, "5.2.4");
  assert.ok(ext.manifest.contributions.tools!.length > 0);
});

test("registry getExtension returns undefined for unknown extension", () => {
  const registry = new ExtensionRegistry();
  assert.equal(registry.getExtension("nonexistent"), undefined);
});

test("registry allRequiredUnityPackageIds returns five package IDs", () => {
  const registry = new ExtensionRegistry();
  const ids = registry.allRequiredUnityPackageIds();
  assert.ok(ids.includes("com.unity.probuilder"));
  assert.ok(ids.includes("com.unity.visualeffectgraph"));
  assert.ok(ids.includes("com.unity.cinemachine"));
  assert.ok(ids.includes("com.unity.render-pipelines.universal"));
  assert.ok(ids.includes("com.unity.render-pipelines.high-definition"));
  assert.equal(ids.length, 5);
});

// ── Registry: duplicate extension ──────────────────────────────────────────

test("registry rejects duplicate extension ID", () => {
  const registry = new ExtensionRegistry();
  assert.throws(
    () => registry.register(PROBUILDER_MANIFEST),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "DUPLICATE_EXTENSION"
  );
});

// ── Registry: unknown extension ────────────────────────────────────────────

test("registry resolveAvailability throws for unknown extension", () => {
  const registry = new ExtensionRegistry();
  assert.throws(
    () => registry.resolveAvailability("nonexistent"),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "UNKNOWN_EXTENSION"
  );
});

// ── Registry: additional manifests ─────────────────────────────────────────

test("registry accepts additional manifests at construction", () => {
  const custom: typeof PROBUILDER_MANIFEST = {
    id: "custom-tool",
    name: "Custom Tool",
    description: "A custom extension for testing.",
    version: "0.1.0",
    unityPackages: [{ packageId: "com.unity.custom", displayName: "Custom" }],
    contributions: { tools: [] }
  };
  const registry = new ExtensionRegistry([custom]);
  assert.ok(registry.hasExtension("custom-tool"));
  assert.equal(registry.extensionIds().length, 6);
});

// ── Registry: manifest validation ──────────────────────────────────────────

test("validateManifest rejects empty id", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, id: "" }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects non-kebab-case id", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, id: "ProBuilder" }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
  for (const id of ["pro_builder", "probuilder\n", "probuilder\r\n", `a${"b".repeat(128)}`]) {
    assert.throws(
      () => validateManifest({ ...PROBUILDER_MANIFEST, id }),
      (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
    );
  }
});

test("validateManifest rejects missing name", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, name: "" }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects missing description", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, description: "" }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects missing version", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, version: "" }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects missing unityPackages", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, unityPackages: undefined as unknown as typeof PROBUILDER_MANIFEST.unityPackages }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects unityPackages entry missing packageId", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, unityPackages: [{ displayName: "Test" }] as unknown as typeof PROBUILDER_MANIFEST.unityPackages }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects unityPackages entry missing displayName", () => {
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, unityPackages: [{ packageId: "com.test" }] as unknown as typeof PROBUILDER_MANIFEST.unityPackages }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects a non-exact package version constraint", () => {
  assert.throws(
    () => validateManifest({
      ...PROBUILDER_MANIFEST,
      unityPackages: [{ packageId: "com.unity.probuilder", displayName: "ProBuilder", versionRange: "^5.2.4" }]
    }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest rejects incomplete tool contribution", () => {
  assert.throws(
    () => validateManifest({
      ...PROBUILDER_MANIFEST,
      contributions: { tools: [{}] as unknown as typeof PROBUILDER_MANIFEST.contributions.tools }
    }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
});

test("validateManifest accepts valid manifest", () => {
  // Should not throw
  validateManifest(PROBUILDER_MANIFEST);
  validateManifest(VFX_GRAPH_MANIFEST);
  validateManifest(CINEMACHINE_MANIFEST);
  validateManifest(URP_MANIFEST);
  validateManifest(HDRP_MANIFEST);
});

// ── Availability resolution ────────────────────────────────────────────────

test("resolveAvailability returns available when all packages installed", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder", {
    "com.unity.probuilder": "5.2.4"
  });
  assert.equal(result.status, "available");
  assert.equal(result.missingPackageIds.length, 0);
  assert.equal(result.nextActions.length, 0);
  assert.equal(result.packages.length, 1);
  assert.equal(result.packages[0].installed, true);
  assert.equal(result.packages[0].version, "5.2.4");
});

test("resolveAvailability rejects a present package outside the exact extension profile", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder", {
    "com.unity.probuilder": "5.2.3"
  });
  assert.equal(result.status, "unavailable");
  assert.deepEqual(result.missingPackageIds, ["com.unity.probuilder"]);
  assert.equal(result.packages[0].installed, false);
  assert.equal(result.packages[0].version, "5.2.3");
  assert.ok(result.nextActions.some((action) => action.includes("com.unity.probuilder@5.2.4")));
});

test("resolveAvailability returns unavailable when package missing", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder", {});
  assert.equal(result.status, "unavailable");
  assert.deepEqual(result.missingPackageIds, ["com.unity.probuilder"]);
  assert.ok(result.nextActions.length > 0);
});

test("resolveAvailability returns unknown when no packages provided", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder");
  assert.equal(result.status, "unknown");
  assert.deepEqual(result.missingPackageIds, ["com.unity.probuilder"]);
  assert.ok(result.nextActions.length > 0);
});

test("resolveAvailability returns unavailable with empty Map (no packages installed)", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder", new Map());
  // Empty Map = we checked the project, nothing installed → unavailable
  assert.equal(result.status, "unavailable");
});

test("resolveAvailability works with Map input", () => {
  const registry = new ExtensionRegistry();
  const installed = new Map([["com.unity.probuilder", "5.2.4"]]);
  const result = registry.resolveAvailability("probuilder", installed);
  assert.equal(result.status, "available");
});

test("resolveAvailability next actions suggest manual package install and extension install", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder", {});
  assert.equal(result.status, "unavailable");
  assert.ok(result.nextActions.some((a) => a.includes("Unity Package Manager")));
  assert.ok(result.nextActions.some((a) => a.includes("manifest.json")));
  assert.ok(result.nextActions.some((a) => a.includes("explicit package versions")));
  assert.equal(result.nextActions.some((a) => a.includes('"latest"')), true, "nextActions should warn against writing latest");
  assert.ok(result.nextActions.some((a) => a.includes("unitycli extensions install probuilder")));
});

test("resolveAllAvailability resolves all extensions", () => {
  const registry = new ExtensionRegistry();
  const results = registry.resolveAllAvailability({
    "com.unity.probuilder": "5.2.4",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.equal(results.length, 5);

  const probuilderResult = results.find((r) => r.extensionId === "probuilder");
  assert.ok(probuilderResult);
  assert.equal(probuilderResult.status, "available");

  const cinemachineResult = results.find((r) => r.extensionId === "cinemachine");
  assert.ok(cinemachineResult);
  assert.equal(cinemachineResult.status, "unavailable");
});

test("availableExtensions filters to available only", () => {
  const registry = new ExtensionRegistry();
  const available = registry.availableExtensions({
    "com.unity.probuilder": "5.2.4",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.equal(available.length, 2);
  assert.ok(available.every((r) => r.status === "available"));
});

test("unavailableExtensions filters to unavailable only", () => {
  const registry = new ExtensionRegistry();
  const unavailable = registry.unavailableExtensions({
    "com.unity.probuilder": "5.2.4",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.equal(unavailable.length, 3);
  assert.ok(unavailable.every((r) => r.status === "unavailable"));
});

// ── Standalone resolveExtensionAvailability ─────────────────────────────────

test("resolveExtensionAvailability works without registry", () => {
  const result = resolveExtensionAvailability(PROBUILDER_MANIFEST, {
    "com.unity.probuilder": "5.2.4"
  });
  assert.equal(result.status, "available");
  assert.equal(result.extensionId, "probuilder");
});

// ── Descriptor Loader ──────────────────────────────────────────────────────

test("loadAllExtensionDescriptors loads all five", () => {
  const registry = new ExtensionRegistry();
  const descriptors = loadAllExtensionDescriptors(registry);
  assert.equal(descriptors.length, 5);
});

test("loadExtensionDescriptor loads a specific extension", () => {
  const registry = new ExtensionRegistry();
  const desc = loadExtensionDescriptor(registry, "probuilder", {
    "com.unity.probuilder": "5.2.4"
  });
  assert.equal(desc.manifest.id, "probuilder");
  assert.equal(desc.availability.status, "available");
  assert.equal(desc.tools.length, 37);
  assert.equal(Object.hasOwn(desc, "contexts"), false);
  assert.equal(desc.csharpEntries.length, 37);
  assert.ok(desc.csharpEntries.some((entry) => entry.type === "UnityCli.ProBuilder.ProBuilderSceneTool"));
});

test("loadExtensionDescriptor for unavailable extension has unavailable status", () => {
  const registry = new ExtensionRegistry();
  const desc = loadExtensionDescriptor(registry, "probuilder", {});
  assert.equal(desc.availability.status, "unavailable");
  assert.equal(desc.tools.length, 37);
  assert.ok(desc.availability.missingPackageIds.length > 0);
});

test("loadExtensionDescriptor throws for unknown extension", () => {
  const registry = new ExtensionRegistry();
  assert.throws(
    () => loadExtensionDescriptor(registry, "nonexistent"),
    /Extension not found/
  );
});

test("loadAvailableDescriptors filters to available", () => {
  const registry = new ExtensionRegistry();
  const available = loadAvailableDescriptors(registry, {
    "com.unity.probuilder": "5.2.4"
  });
  assert.equal(available.length, 1);
  assert.equal(available[0].manifest.id, "probuilder");
});

test("loadUnavailableDescriptors filters to unavailable", () => {
  const registry = new ExtensionRegistry();
  const unavailable = loadUnavailableDescriptors(registry, {
    "com.unity.probuilder": "5.2.4"
  });
  assert.ok(unavailable.length > 0);
  assert.ok(unavailable.every((d) => d.availability.status === "unavailable"));
});

// ── collectAvailableTools ──────────────────────────────────────────────────

test("collectAvailableTools returns available extension tools and built-in VFX component tools", () => {
  const registry = new ExtensionRegistry();
  const tools = collectAvailableTools(registry, {
    "com.unity.probuilder": "5.2.4"
  });
  const toolNames = tools.map((t) => t.name);
  assert.equal(toolNames.filter((name) => name.startsWith("probuilder.")).length, 37);
  assert.equal(toolNames.filter((name) => name.startsWith("vfx.")).length, VFX_GRAPH_MANIFEST.contributions.tools!.filter((tool) => !(tool.requiresPackages ?? []).includes("com.unity.visualeffectgraph")).length);
  assert.ok(toolNames.includes("vfx.particle-create"));
  assert.ok(toolNames.includes("vfx.line-create"));
  assert.ok(toolNames.includes("vfx.trail.create"));
  assert.ok(toolNames.includes("vfx.trail.set"));
  assert.ok(tools.filter((tool) => tool.name.startsWith("vfx.")).every((tool) => !(tool.requiresPackages ?? []).includes("com.unity.visualeffectgraph")));
});

test("collectAvailableTools returns built-in VFX component tools when optional packages are absent", () => {
  const registry = new ExtensionRegistry();
  const tools = collectAvailableTools(registry, {});
  assert.deepEqual(tools.map((t) => t.name), [
    "vfx.particle-create",
    "vfx.particle-info",
    "vfx.particle-main",
    "vfx.particle-emission",
    "vfx.particle-shape",
    "vfx.particle.play",
    "vfx.particle.stop",
    "vfx.particle.pause",
    "vfx.particle.clear",
    "vfx.line-create",
    "vfx.line-positions",
    "vfx.trail.create",
    "vfx.trail.set"
  ]);
});

test("collectAllTools includes unavailable extension tools with availability flag", () => {
  const registry = new ExtensionRegistry();
  const tools = collectAllTools(registry, {
    "com.unity.probuilder": "5.2.4"
  });
  assert.ok(tools.length >= 37); // At least ProBuilder's tools

  const probuilderTools = tools.filter((t) => t.extensionId === "probuilder");
  assert.equal(probuilderTools.length, 37);
  assert.ok(probuilderTools.every((t) => t.available));

  // Cinemachine exposes one descriptor per manifest contribution.
  const cinemachineTools = tools.filter((t) => t.extensionId === "cinemachine");
  assert.equal(cinemachineTools.length, CINEMACHINE_MANIFEST.contributions.tools!.length);
  assert.ok(cinemachineTools.every((t) => !t.available)); // Not installed
});

// ── Docs / CSharpEntries ────────────────────────────────────────────────────

test("collectAvailableCSharpEntries returns entries from available extensions", () => {
  const registry = new ExtensionRegistry();
  const entries = collectAvailableCSharpEntries(registry, {
    "com.unity.probuilder": "5.2.4",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.ok(entries.some((e) => e.type.startsWith("UnityCli.ProBuilder.")));
  assert.ok(entries.some((e) => e.type === "UnityCli.VfxGraphInfoTool"));
  assert.ok(entries.some((e) => e.type === "UnityCli.VfxGraphCreateTool"));
  assert.ok(entries.some((e) => e.type === "UnityCli.VfxTrailCreateTool"));
  assert.ok(entries.some((e) => e.type === "UnityCli.VfxTrailSetTool"));
  assert.equal(entries.filter((e) => e.type.startsWith("UnityCli.Vfx")).length, VFX_GRAPH_MANIFEST.contributions.csharpEntries!.length);
});

// ── extensionAvailabilitySummary ───────────────────────────────────────────

test("extensionAvailabilitySummary provides correct counts", () => {
  const registry = new ExtensionRegistry();
  const summary = extensionAvailabilitySummary(registry, {
    "com.unity.probuilder": "5.2.4",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.equal(summary.total, 5);
  assert.equal(summary.available, 2);
  assert.equal(summary.unavailable, 3);
  assert.equal(summary.unknown, 0);
});

test("extensionAvailabilitySummary shows unknown when no packages provided", () => {
  const registry = new ExtensionRegistry();
  const summary = extensionAvailabilitySummary(registry);
  // No installedPackages argument → unknown (no project context)
  assert.equal(summary.total, 5);
  assert.equal(summary.unknown, 5);
  assert.equal(summary.available, 0);
});

// ── Project resolver ───────────────────────────────────────────────────────

test("readInstalledUnityPackages reads manifest and lock from project", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));

  // Create Packages/manifest.json
  await mkdir(join(projectDir, "Packages"), { recursive: true });
  await writeFile(
    join(projectDir, "Packages", "manifest.json"),
    JSON.stringify({
      dependencies: {
        "com.unity.probuilder": "5.2.4",
        "com.unity.visualeffectgraph": "14.0.0"
      }
    })
  );

  // Create Packages/packages-lock.json
  await writeFile(
    join(projectDir, "Packages", "packages-lock.json"),
    JSON.stringify({
      dependencies: {
        "com.unity.probuilder": { version: "5.2.2" },
        "com.unity.visualeffectgraph": { version: "14.0.5" },
        "com.unity.timeline": { version: "1.7.6" }
      }
    })
  );

  const packages = await readInstalledUnityPackages(projectDir);
  assert.equal(packages.get("com.unity.probuilder"), "5.2.2"); // Lock overrides manifest
  assert.equal(packages.get("com.unity.visualeffectgraph"), "14.0.5");
  assert.equal(packages.get("com.unity.timeline"), "1.7.6"); // Lock-only package
  assert.equal(packages.size, 3);
});

test("readInstalledUnityPackages handles missing lock file", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));

  await mkdir(join(projectDir, "Packages"), { recursive: true });
  await writeFile(
    join(projectDir, "Packages", "manifest.json"),
    JSON.stringify({
      dependencies: { "com.unity.probuilder": "5.0.0" }
    })
  );

  // When packages-lock.json is missing, no packages are considered installed.
  // manifest.json declares intent but only packages-lock.json records resolved packages.
  const packages = await readInstalledUnityPackages(projectDir);
  assert.equal(packages.size, 0);
});

test("readInstalledUnityPackages handles missing manifest file", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));
  const packages = await readInstalledUnityPackages(projectDir);
  assert.equal(packages.size, 0);
});

test("readInstalledUnityPackages handles BOM in JSON", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));

  await mkdir(join(projectDir, "Packages"), { recursive: true });
  const bom = "\uFEFF";
  // Test BOM handling in packages-lock.json (the authoritative source)
  await writeFile(
    join(projectDir, "Packages", "packages-lock.json"),
    bom + JSON.stringify({
      dependencies: { "com.unity.probuilder": { version: "5.2.4" } }
    })
  );

  const packages = await readInstalledUnityPackages(projectDir);
  assert.equal(packages.get("com.unity.probuilder"), "5.2.4");
});

test("resolveExtensionAvailabilityForProject resolves against real project", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));

  await mkdir(join(projectDir, "Packages"), { recursive: true });
  await writeFile(
    join(projectDir, "Packages", "manifest.json"),
    JSON.stringify({
      dependencies: { "com.unity.probuilder": "5.2.4" }
    })
  );
  await writeFile(
    join(projectDir, "Packages", "packages-lock.json"),
    JSON.stringify({
      dependencies: { "com.unity.probuilder": { version: "5.2.4" } }
    })
  );

  const registry = new ExtensionRegistry();
  const results = await resolveExtensionAvailabilityForProject(registry, projectDir);

  const probuilder = results.find((r) => r.extensionId === "probuilder");
  assert.ok(probuilder);
  assert.equal(probuilder.status, "available");

  const cinemachine = results.find((r) => r.extensionId === "cinemachine");
  assert.ok(cinemachine);
  assert.equal(cinemachine.status, "unavailable");
});

// ── Registry data: specific manifest content checks ────────────────────────

test("ProBuilder manifest has correct tool contributions", () => {
  assert.equal(PROBUILDER_MANIFEST.contributions.tools!.length, 37);
  const toolNames = PROBUILDER_MANIFEST.contributions.tools!.map((t) => t.name);
  assert.ok(toolNames.includes("probuilder.ping"));
  assert.ok(toolNames.includes("probuilder.create-shape"));
  assert.ok(toolNames.includes("probuilder.extrude-faces"));
  assert.ok(toolNames.includes("probuilder.repair-mesh"));
  // Old 4 coarse-grained tools should NOT be present
  assert.ok(!toolNames.includes("probuilder.shape"));
  assert.ok(!toolNames.includes("probuilder.create"));
  assert.ok(!toolNames.includes("probuilder.edit"));
  assert.ok(!toolNames.includes("probuilder.csg"));
});

test("VFX Graph manifest has one contribution per operation", () => {
  assert.equal(VFX_GRAPH_MANIFEST.contributions.tools!.length, VFX_GRAPH_MANIFEST.contributions.csharpEntries!.length);
  const toolNames = VFX_GRAPH_MANIFEST.contributions.tools!.map((t) => t.name);
  assert.ok(toolNames.includes("vfx.graph.info"));
  assert.ok(toolNames.includes("vfx.graph.create"));
  assert.ok(toolNames.includes("vfx.effect.info"));
  assert.ok(toolNames.includes("vfx.effect.add"));
  assert.ok(toolNames.includes("vfx.effect.assign"));
  assert.ok(toolNames.includes("vfx.control.set-seed"));
  assert.ok(toolNames.includes("vfx.particle.play"));
  assert.ok(toolNames.includes("vfx.particle.clear"));
  assert.ok(toolNames.includes("vfx.template.list"));
  assert.ok(toolNames.includes("vfx.template.create"));
  assert.ok(toolNames.includes("vfx.trail.create"));
  assert.ok(toolNames.includes("vfx.trail.set"));
  const graphOnly = VFX_GRAPH_MANIFEST.contributions.tools!.filter((t) => t.requiresPackages?.includes("com.unity.visualeffectgraph"));
  assert.equal(graphOnly.length, VFX_GRAPH_MANIFEST.contributions.tools!.filter((tool) => tool.requiresPackages?.includes("com.unity.visualeffectgraph")).length);
});

test("Cinemachine manifest has one C# entry per Tool", () => {
  assert.equal(CINEMACHINE_MANIFEST.contributions.tools!.length, CINEMACHINE_MANIFEST.contributions.csharpEntries!.length);
  assert.equal(Object.hasOwn(CINEMACHINE_MANIFEST.contributions, "contexts"), false);
  const toolNames = CINEMACHINE_MANIFEST.contributions.tools!.map((t) => t.name);
  assert.ok(toolNames.includes("cinemachine.ping"));
});

test("render-pipeline volume setters support Unity 2022 generic TryGet API", async () => {
  for (const file of [
    "extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs",
    "extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs"
  ]) {
    const source = await readFile(file, "utf8");
    assert.match(source, /profile\.components\.FirstOrDefault/);
    assert.doesNotMatch(source, /GetMethod\("TryGet"/);
  }
});

test("extension mutating tools verify observed state instead of echoing requests", async () => {
  const sources = await Promise.all([
    "extensions/urp/unitypkg/Editor/Urp/UrpToolSupport.cs",
    "extensions/hdrp/unitypkg/Editor/Hdrp/HdrpToolSupport.cs",
    "extensions/urp/unitypkg/Editor/Urp/UrpFeatureToolSupport.cs",
    "extensions/vfx-graph/unitypkg/Editor/Vfx/VfxActionToolSupport.cs",
    "extensions/vfx-graph/unitypkg/Editor/Vfx/parameter.cs",
    "extensions/cinemachine/unitypkg/Editor/Cinemachine/CinemachineExtensionToolSupport.cs"
  ].map((file) => readFile(file, "utf8")));
  for (const source of sources.slice(0, 2)) {
    assert.match(source, /(?:serialized|observedSerialized)\.Update\(\)/);
    assert.match(source, /VOLUME_VERIFY_FAILED/);
    assert.match(source, /ReadSerializedValue/);
  }
  assert.match(sources[2], /RequireFeature\(featureIndex, rendererIndex, "urp\.feature\.toggle"\)/);
  assert.match(sources[2], /ContainsFeature/);
  assert.match(sources[2], /ReadSerializedPropertyValue/);
  assert.match(sources[3], /SetAndReadRequired/);
  assert.match(sources[4], /Has" \+ typeName/);
  assert.match(sources[4], /Get" \+ typeName/);
  assert.doesNotMatch(sources[4], /if \(method != null\)/);
  assert.match(sources[5], /GetComponents\(CinemachineToolSupport\.ExtensionBaseType\)/);
  assert.match(sources[5], /ResolveRequiredTarget\(targetId\.ToString/);
});

test("URP manifest has one tool per feature, volume, and settings operation", () => {
  assert.equal(URP_MANIFEST.contributions.tools!.length, 12);
  assert.deepEqual(URP_MANIFEST.contributions.tools!.filter((tool) => tool.name.startsWith("urp.feature.")).map((tool) => tool.name), ["urp.feature.list", "urp.feature.add", "urp.feature.toggle", "urp.feature.configure", "urp.feature.remove"]);
  assert.deepEqual(URP_MANIFEST.contributions.tools!.filter((tool) => tool.name.startsWith("urp.volume.")).map((tool) => tool.name), ["urp.volume.list", "urp.volume.create", "urp.volume.info", "urp.volume.add-effect", "urp.volume.set"]);
  assert.deepEqual(URP_MANIFEST.contributions.tools!.filter((tool) => tool.name.startsWith("urp.settings.")).map((tool) => tool.name), ["urp.settings.get", "urp.settings.set"]);
  assert.equal(Object.hasOwn(URP_MANIFEST.contributions, "contexts"), false);
});

test("HDRP manifest has one tool per volume and settings operation", () => {
  assert.equal(HDRP_MANIFEST.contributions.tools!.length, 7);
  assert.deepEqual(HDRP_MANIFEST.contributions.tools!.filter((tool) => tool.name.startsWith("hdrp.volume.")).map((tool) => tool.name), ["hdrp.volume.list", "hdrp.volume.create", "hdrp.volume.info", "hdrp.volume.add-effect", "hdrp.volume.set"]);
  assert.deepEqual(HDRP_MANIFEST.contributions.tools!.filter((tool) => tool.name.startsWith("hdrp.settings.")).map((tool) => tool.name), ["hdrp.settings.get", "hdrp.settings.set"]);
  assert.equal(Object.hasOwn(HDRP_MANIFEST.contributions, "contexts"), false);
});

test("all five manifests have unique IDs", () => {
  const ids = BUILTIN_EXTENSION_MANIFESTS.map((m) => m.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("extension manifests declare bundle-level Unity package gates only when required", () => {
  for (const m of BUILTIN_EXTENSION_MANIFESTS) {
    if (m.id === "vfx-graph") {
      assert.equal(m.unityPackages.length, 0, "vfx-graph bundle must install built-in component tools without com.unity.visualeffectgraph");
    } else {
      assert.ok(m.unityPackages.length >= 1, `${m.id} should have at least one unityPackage`);
    }
  }
});


test("built-in manifests declare Unity directory install layout", () => {
  for (const manifest of BUILTIN_EXTENSION_MANIFESTS) {
    assert.equal(manifest.unityInstall?.layout, "directory", `${manifest.id} should install as a Unity extension directory`);
    assert.deepEqual(manifest.unityInstall?.include, ["extension.json", "unitypkg/", "docs/"]);
  }
});

test("each extension compiles in its own Editor assembly", async () => {
  const assemblyFiles = new Map([
    ["cinemachine", "UnityCli.Extensions.Cinemachine.Editor.asmdef"],
    ["hdrp", "UnityCli.Extensions.Hdrp.Editor.asmdef"],
    ["probuilder", "UnityCli.Extensions.ProBuilder.Editor.asmdef"],
    ["urp", "UnityCli.Extensions.Urp.Editor.asmdef"],
    ["vfx-graph", "UnityCli.Extensions.VfxGraph.Editor.asmdef"]
  ]);

  for (const [extensionId, fileName] of assemblyFiles) {
    const path = join(process.cwd(), "extensions", extensionId, "unitypkg", fileName);
    const assembly = JSON.parse(await readFile(path, "utf8")) as { references?: string[]; includePlatforms?: string[] };
    assert.ok(assembly.references?.includes("UnityCli.Editor"), `${extensionId} must reference the core Editor assembly`);
    assert.deepEqual(assembly.includePlatforms, ["Editor"]);
    const requiresCoreRuntime = extensionId === "urp" || extensionId === "hdrp";
    assert.equal(assembly.references?.includes("Unity.RenderPipelines.Core.Runtime"), requiresCoreRuntime);
  }
});

test("extension install manifest rejects incomplete managed file entries", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));
  try {
    await mkdir(join(projectDir, "Packages", "com.alpsckr.unitycli"), { recursive: true });
    await writeFile(join(projectDir, "Packages", "com.alpsckr.unitycli", "unitycli.extensions.manifest.json"), JSON.stringify({
      version: 1,
      extensions: [{ extensionId: "probuilder", version: "1.0.0", installedAt: new Date().toISOString(), sourceHash: "0".repeat(64), files: [{ path: "Editor/test.cs" }], requiresUnityPackages: ["com.unity.probuilder"], copyRoot: "extension-directory", layout: "directory", installMode: "copy-if-package-present" }]
    }));
    await assert.rejects(() => readExtensionInstallManifest(projectDir), { code: "E_EXTENSION_INSTALL_MANIFEST_INVALID" });
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("extension install manifest rejects duplicate extension ids", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));
  try {
    await mkdir(join(projectDir, "Packages", "com.alpsckr.unitycli"), { recursive: true });
    const entry = { extensionId: "probuilder", version: "1.0.0", installedAt: new Date().toISOString(), sourceHash: "0".repeat(64), files: [], requiresUnityPackages: ["com.unity.probuilder"], copyRoot: "extension-directory", layout: "directory", installMode: "copy-if-package-present" };
    await writeFile(join(projectDir, "Packages", "com.alpsckr.unitycli", "unitycli.extensions.manifest.json"), JSON.stringify({ version: 1, extensions: [entry, entry] }));
    await assert.rejects(() => readExtensionInstallManifest(projectDir), { code: "E_EXTENSION_INSTALL_MANIFEST_INVALID" });
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("extension source hash and managed files describe the same staged projection", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));
  try {
    await mkdir(join(projectDir, "Packages"), { recursive: true });
    await writeFile(join(projectDir, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }));
    await writeFile(join(projectDir, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }));
    await installExtensionBundle(projectDir, VFX_GRAPH_MANIFEST);
    const entry = (await readExtensionInstallManifest(projectDir)).extensions.find((item) => item.extensionId === "vfx-graph")!;
    const crypto = await import("node:crypto");
    const hash = crypto.createHash("sha256");
    for (const file of entry.files) {
      hash.update(`file:${file.path}\n`);
      hash.update(await readFile(join(extensionInstallRoot(projectDir, "vfx-graph"), file.path)));
      hash.update("\n");
    }
    assert.equal(entry.sourceHash, hash.digest("hex"));
  } finally {
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("extension install rejects a source mutation before publishing the staging projection", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));
  const sourceRoot = await mkdtemp(join(tmpdir(), "unity-cli-ext-source-"));
  await cp(extensionSourceRoot("vfx-graph"), sourceRoot, { recursive: true });
  const sourceFile = join(sourceRoot, "extension.json");
  const original = await readFile(sourceFile, "utf8");
  try {
    await mkdir(join(projectDir, "Packages"), { recursive: true });
    await writeFile(join(projectDir, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }));
    await writeFile(join(projectDir, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }));
    setExtensionSourceRootForTest(() => sourceRoot);
    setAfterProjectionCopyForTest(async () => { await writeFile(sourceFile, `${original}\n`, "utf8"); });
    await assert.rejects(() => installExtensionBundle(projectDir, VFX_GRAPH_MANIFEST), { code: "E_EXTENSION_SOURCE_SNAPSHOT_STALE" });
    await assert.rejects(() => stat(extensionInstallRoot(projectDir, "vfx-graph")), { code: "ENOENT" });
  } finally {
    setAfterProjectionCopyForTest(undefined);
    setExtensionSourceRootForTest(undefined);
    await rm(sourceRoot, { recursive: true, force: true });
    await rm(projectDir, { recursive: true, force: true });
  }
});

test("extension bundle checks ignore Unity-generated .meta files", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));
  await mkdir(join(projectDir, "Packages"), { recursive: true });
  await writeFile(join(projectDir, "Packages", "manifest.json"), JSON.stringify({ dependencies: {} }));
  await writeFile(join(projectDir, "Packages", "packages-lock.json"), JSON.stringify({ dependencies: {} }));

  await installExtensionBundle(projectDir, VFX_GRAPH_MANIFEST);
  await writeFile(join(extensionInstallRoot(projectDir, "vfx-graph"), "Editor.meta"), "fileFormatVersion: 2\n");

  const checks = await checkExtensionBundles(projectDir);
  const vfx = checks.find((check) => check.extensionId === "vfx-graph");
  assert.ok(vfx);
  assert.equal(vfx.status, "installed");
  assert.equal(vfx.installed, true);
  assert.deepEqual(vfx.unknownFiles, []);
});
test("all five manifests have valid contributions", () => {
  for (const m of BUILTIN_EXTENSION_MANIFESTS) {
    assert.ok(m.contributions, `${m.id} should have contributions`);
    assert.ok(Array.isArray(m.contributions.tools), `${m.id} contributions.tools should be array`);
    assert.equal(Object.hasOwn(m.contributions, "contexts"), false, `${m.id} must expose only Tool contributions`);
    assert.ok(Array.isArray(m.contributions.csharpEntries), `${m.id} contributions.csharpEntries should be array`);
  }
});

test("extension schemas expose only Tool contributions", async () => {
  const [descriptorSchema, manifestSchema] = await Promise.all([
    readFile(join(process.cwd(), "schemas", "extension-descriptor.schema.json"), "utf8"),
    readFile(join(process.cwd(), "schemas", "extension-manifest.schema.json"), "utf8"),
  ]);
  assert.doesNotMatch(descriptorSchema, /providesContexts/);
  assert.doesNotMatch(manifestSchema, /"contexts"\s*:/);
});

// ── Core package independence ──────────────────────────────────────────────

test("core package extension types do not reference optional Unity packages at compile time", () => {
  // This is a structural test: the ExtensionManifest.unityPackages field
  // is data-driven, not an import. The core types module should not import
  // any package-specific code.
  //
  // We verify this by checking that the types.ts module has no references
  // to com.unity.* package strings.
  const typesModule = `
    ExtensionId, ExtensionAvailability, UnityPackageRequirement,
    ExtensionToolContribution, ExtensionCSharpEntry,
    ExtensionContributions, ExtensionManifest,
    PackageResolution, ExtensionAvailabilityResult, RegistryExtension,
    LoadedExtensionDescriptor
  `;
  assert.ok(!typesModule.includes("com.unity."));
  assert.ok(!typesModule.includes("probuilder"));
  assert.ok(!typesModule.includes("cinemachine"));
});

// ── Missing dependency state and nextActions ──────────────────────────────

test("unavailable extension expresses missing state with nextActions", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("cinemachine", {});

  assert.equal(result.status, "unavailable");
  assert.deepEqual(result.missingPackageIds, ["com.unity.cinemachine"]);
  assert.ok(result.nextActions.length >= 2);
  assert.ok(result.nextActions.some((a) => a.includes("Unity Package Manager")));
  assert.ok(result.nextActions.some((a) => a.includes("manifest.json")));
  assert.ok(result.nextActions.some((a) => a.includes("extensions install cinemachine")));
  assert.ok(result.nextActions.some((a) => a.includes("extensions doctor")));
});

test("unavailable extension with multiple missing packages lists all", () => {
  // Hypothetical extension with two packages
  const dualPkg: typeof PROBUILDER_MANIFEST = {
    id: "dual-pkg",
    name: "Dual Package",
    description: "Test extension with two packages.",
    version: "0.1.0",
    unityPackages: [
      { packageId: "com.unity.pkg1", displayName: "Package 1" },
      { packageId: "com.unity.pkg2", displayName: "Package 2" }
    ],
    contributions: { tools: [] }
  };
  const registry = new ExtensionRegistry([dualPkg]);
  const result = registry.resolveAvailability("dual-pkg", { "com.unity.pkg1": "1.0.0" });

  assert.equal(result.status, "unavailable");
  assert.deepEqual(result.missingPackageIds, ["com.unity.pkg2"]);
  assert.equal(result.packages.length, 2);
  assert.equal(result.packages[0].installed, true);
  assert.equal(result.packages[1].installed, false);
});

function schemaProperties(schema: unknown): Record<string, unknown> {
  if (!schema || typeof schema !== "object") return {};
  const properties = (schema as { properties?: unknown }).properties;
  return properties && typeof properties === "object" ? properties as Record<string, unknown> : {};
}

test("partially available extension shows per-package resolution", () => {
  const dualPkg: typeof PROBUILDER_MANIFEST = {
    id: "dual-pkg-2",
    name: "Dual Package 2",
    description: "Another test extension with two packages.",
    version: "0.1.0",
    unityPackages: [
      { packageId: "com.unity.pkg-a", displayName: "Package A" },
      { packageId: "com.unity.pkg-b", displayName: "Package B" }
    ],
    contributions: { tools: [] }
  };
  const registry = new ExtensionRegistry([dualPkg]);
  const result = registry.resolveAvailability("dual-pkg-2", {
    "com.unity.pkg-a": "2.0.0",
    "com.unity.pkg-b": "3.0.0"
  });

  assert.equal(result.status, "available");
  assert.equal(result.missingPackageIds.length, 0);
});
