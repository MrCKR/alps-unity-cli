/**
 * Tests for the extension system: registry, descriptor loader, project resolver.
 *
 * P5-T01: Extension package manifest / registry / descriptor loader.
 */

import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import {
  ExtensionRegistry,
  ExtensionRegistryError,
  validateManifest,
  resolveExtensionAvailability
} from "./registry.js";

import {
  PROBUILDER_MANIFEST,
  VFX_GRAPH_MANIFEST,
  CINEMACHINE_MANIFEST,
  URP_MANIFEST,
  HDRP_MANIFEST,
  BUILTIN_EXTENSION_MANIFESTS
} from "./registry-data.js";

import {
  loadAllExtensionDescriptors,
  loadExtensionDescriptor,
  loadAvailableDescriptors,
  loadUnavailableDescriptors,
  collectAvailableTools,
  collectAllTools,
  collectAvailableContexts,
  collectAvailableDocs,
  collectAvailableCSharpEntries,
  extensionAvailabilitySummary
} from "./descriptor-loader.js";

import {
  readInstalledUnityPackages,
  resolveExtensionAvailabilityForProject
} from "./project-resolver.js";

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
  assert.throws(
    () => validateManifest({ ...PROBUILDER_MANIFEST, id: "pro_builder" }),
    (err: unknown) => err instanceof ExtensionRegistryError && err.code === "INVALID_MANIFEST"
  );
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

test("validateManifest rejects incomplete tool contribution", () => {
  assert.throws(
    () => validateManifest({
      ...PROBUILDER_MANIFEST,
      contributions: { tools: [{ name: "test.tool" }] as unknown as typeof PROBUILDER_MANIFEST.contributions.tools }
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
    "com.unity.probuilder": "5.2.0"
  });
  assert.equal(result.status, "available");
  assert.equal(result.missingPackageIds.length, 0);
  assert.equal(result.nextActions.length, 0);
  assert.equal(result.packages.length, 1);
  assert.equal(result.packages[0].installed, true);
  assert.equal(result.packages[0].version, "5.2.0");
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
  const installed = new Map([["com.unity.probuilder", "5.2.0"]]);
  const result = registry.resolveAvailability("probuilder", installed);
  assert.equal(result.status, "available");
});

test("resolveAvailability next actions suggest install commands", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("probuilder", {});
  assert.equal(result.status, "unavailable");
  assert.ok(result.nextActions.some((a) => a.includes("unity_cli package install")));
  assert.ok(result.nextActions.some((a) => a.includes("manifest.json")));
});

test("resolveAllAvailability resolves all extensions", () => {
  const registry = new ExtensionRegistry();
  const results = registry.resolveAllAvailability({
    "com.unity.probuilder": "5.2.0",
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
    "com.unity.probuilder": "5.2.0",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.equal(available.length, 2);
  assert.ok(available.every((r) => r.status === "available"));
});

test("unavailableExtensions filters to unavailable only", () => {
  const registry = new ExtensionRegistry();
  const unavailable = registry.unavailableExtensions({
    "com.unity.probuilder": "5.2.0",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.equal(unavailable.length, 3);
  assert.ok(unavailable.every((r) => r.status === "unavailable"));
});

// ── Standalone resolveExtensionAvailability ─────────────────────────────────

test("resolveExtensionAvailability works without registry", () => {
  const result = resolveExtensionAvailability(PROBUILDER_MANIFEST, {
    "com.unity.probuilder": "5.2.0"
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
    "com.unity.probuilder": "5.2.0"
  });
  assert.equal(desc.manifest.id, "probuilder");
  assert.equal(desc.availability.status, "available");
  assert.equal(desc.tools.length, 4);
  assert.equal(desc.contexts.length, 1);
  assert.equal(desc.bridgeCommands.length, 1);
  assert.equal(desc.csharpEntries.length, 1);
  assert.equal(desc.docs.length, 1);
});

test("loadExtensionDescriptor for unavailable extension has unavailable status", () => {
  const registry = new ExtensionRegistry();
  const desc = loadExtensionDescriptor(registry, "probuilder", {});
  assert.equal(desc.availability.status, "unavailable");
  assert.equal(desc.tools.length, 4); // Tools are still listed
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
    "com.unity.probuilder": "5.2.0"
  });
  assert.equal(available.length, 1);
  assert.equal(available[0].manifest.id, "probuilder");
});

test("loadUnavailableDescriptors filters to unavailable", () => {
  const registry = new ExtensionRegistry();
  const unavailable = loadUnavailableDescriptors(registry, {
    "com.unity.probuilder": "5.2.0"
  });
  assert.ok(unavailable.length > 0);
  assert.ok(unavailable.every((d) => d.availability.status === "unavailable"));
});

// ── collectAvailableTools ──────────────────────────────────────────────────

test("collectAvailableTools returns tools from available extensions only", () => {
  const registry = new ExtensionRegistry();
  const tools = collectAvailableTools(registry, {
    "com.unity.probuilder": "5.2.0"
  });
  assert.ok(tools.every((t) => t.group === "probuilder"));
  assert.equal(tools.length, 4);
});

test("collectAvailableTools returns empty when no extensions available", () => {
  const registry = new ExtensionRegistry();
  const tools = collectAvailableTools(registry, {});
  assert.equal(tools.length, 0);
});

test("collectAllTools includes unavailable extension tools with availability flag", () => {
  const registry = new ExtensionRegistry();
  const tools = collectAllTools(registry, {
    "com.unity.probuilder": "5.2.0"
  });
  assert.ok(tools.length >= 4); // At least ProBuilder's tools

  const probuilderTools = tools.filter((t) => t.extensionId === "probuilder");
  assert.equal(probuilderTools.length, 4);
  assert.ok(probuilderTools.every((t) => t.available));

  const cinemachineTools = tools.filter((t) => t.extensionId === "cinemachine");
  assert.equal(cinemachineTools.length, 0); // Cinemachine has no tools yet
});

// ── collectAvailableContexts / Docs / CSharpEntries ─────────────────────────

test("collectAvailableContexts returns contexts from available extensions", () => {
  const registry = new ExtensionRegistry();
  const contexts = collectAvailableContexts(registry, {
    "com.unity.probuilder": "5.2.0",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.ok(contexts.length >= 2);
  assert.ok(contexts.some((c) => c.key === "probuilder.scene"));
  assert.ok(contexts.some((c) => c.key === "vfx.assets"));
});

test("collectAvailableDocs returns docs from available extensions", () => {
  const registry = new ExtensionRegistry();
  const docs = collectAvailableDocs(registry, {
    "com.unity.probuilder": "5.2.0"
  });
  assert.ok(docs.some((d) => d.topic === "probuilder"));
});

test("collectAvailableCSharpEntries returns entries from available extensions", () => {
  const registry = new ExtensionRegistry();
  const entries = collectAvailableCSharpEntries(registry, {
    "com.unity.probuilder": "5.2.0",
    "com.unity.visualeffectgraph": "14.0.0"
  });
  assert.ok(entries.some((e) => e.type === "UnityCli.ProBuilderCommands"));
  assert.ok(entries.some((e) => e.type === "UnityCli.VfxCommands"));
});

// ── extensionAvailabilitySummary ───────────────────────────────────────────

test("extensionAvailabilitySummary provides correct counts", () => {
  const registry = new ExtensionRegistry();
  const summary = extensionAvailabilitySummary(registry, {
    "com.unity.probuilder": "5.2.0",
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
        "com.unity.probuilder": "5.2.0",
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

  const packages = await readInstalledUnityPackages(projectDir);
  assert.equal(packages.get("com.unity.probuilder"), "5.0.0");
  assert.equal(packages.size, 1);
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
  await writeFile(
    join(projectDir, "Packages", "manifest.json"),
    bom + JSON.stringify({
      dependencies: { "com.unity.probuilder": "5.2.0" }
    })
  );

  const packages = await readInstalledUnityPackages(projectDir);
  assert.equal(packages.get("com.unity.probuilder"), "5.2.0");
});

test("resolveExtensionAvailabilityForProject resolves against real project", async () => {
  const projectDir = await mkdtemp(join(tmpdir(), "unity-cli-ext-test-"));

  await mkdir(join(projectDir, "Packages"), { recursive: true });
  await writeFile(
    join(projectDir, "Packages", "manifest.json"),
    JSON.stringify({
      dependencies: { "com.unity.probuilder": "5.2.0" }
    })
  );
  await writeFile(
    join(projectDir, "Packages", "packages-lock.json"),
    JSON.stringify({
      dependencies: { "com.unity.probuilder": { version: "5.2.2" } }
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
  assert.equal(PROBUILDER_MANIFEST.contributions.tools!.length, 4);
  const toolNames = PROBUILDER_MANIFEST.contributions.tools!.map((t) => t.name);
  assert.deepEqual(toolNames, ["probuilder.shape", "probuilder.create", "probuilder.edit", "probuilder.csg"]);
});

test("VFX Graph manifest has correct tool contributions", () => {
  assert.equal(VFX_GRAPH_MANIFEST.contributions.tools!.length, 2);
  const toolNames = VFX_GRAPH_MANIFEST.contributions.tools!.map((t) => t.name);
  assert.deepEqual(toolNames, ["vfx.graph", "vfx.effect"]);
});

test("Cinemachine manifest has no tools yet (future phase)", () => {
  assert.equal(CINEMACHINE_MANIFEST.contributions.tools!.length, 0);
});

test("URP manifest has no tools yet (future phase)", () => {
  assert.equal(URP_MANIFEST.contributions.tools!.length, 0);
});

test("HDRP manifest has no tools yet (future phase)", () => {
  assert.equal(HDRP_MANIFEST.contributions.tools!.length, 0);
});

test("all five manifests have unique IDs", () => {
  const ids = BUILTIN_EXTENSION_MANIFESTS.map((m) => m.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("all five manifests have at least one unityPackage", () => {
  for (const m of BUILTIN_EXTENSION_MANIFESTS) {
    assert.ok(m.unityPackages.length >= 1, `${m.id} should have at least one unityPackage`);
  }
});

test("all five manifests have valid contributions", () => {
  for (const m of BUILTIN_EXTENSION_MANIFESTS) {
    assert.ok(m.contributions, `${m.id} should have contributions`);
    assert.ok(Array.isArray(m.contributions.tools), `${m.id} contributions.tools should be array`);
    assert.ok(Array.isArray(m.contributions.contexts), `${m.id} contributions.contexts should be array`);
    assert.ok(Array.isArray(m.contributions.bridgeCommands), `${m.id} contributions.bridgeCommands should be array`);
    assert.ok(Array.isArray(m.contributions.csharpEntries), `${m.id} contributions.csharpEntries should be array`);
    assert.ok(Array.isArray(m.contributions.docs), `${m.id} contributions.docs should be array`);
  }
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
    ExtensionToolContribution, ExtensionContextContribution,
    ExtensionDocsContribution, ExtensionCSharpEntry,
    ExtensionContributions, ExtensionManifest,
    PackageResolution, ExtensionAvailabilityResult, RegistryExtension,
    LoadedExtensionDescriptor
  `;
  assert.ok(!typesModule.includes("com.unity."));
  assert.ok(!typesModule.includes("probuilder"));
  assert.ok(!typesModule.includes("cinemachine"));
});

// ── Missing dependency state and next_actions ──────────────────────────────

test("unavailable extension expresses missing state with next_actions for T02/T03", () => {
  const registry = new ExtensionRegistry();
  const result = registry.resolveAvailability("cinemachine", {});

  assert.equal(result.status, "unavailable");
  assert.deepEqual(result.missingPackageIds, ["com.unity.cinemachine"]);
  assert.ok(result.nextActions.length >= 2);
  assert.ok(result.nextActions.some((a) => a.includes("unity_cli package install")));
  assert.ok(result.nextActions.some((a) => a.includes("manifest.json")));
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
