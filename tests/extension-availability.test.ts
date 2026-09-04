import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, writeFile, mkdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { handleToolsCommand, type ToolsRuntime } from "../src/tools/commands.js";
import { handleDoctorCommand, type DoctorRuntime } from "../src/doctor.js";
import { ExtensionRegistry, BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import {
  checkExtensionBundles,
  installExtensionBundle,
  listExtensionBundles,
  readExtensionInstallManifest,
  setExtensionFilesystemClockForTest,
  setExtensionFilesystemOperationObserverForTest,
} from "../src/extensions/installer.js";
import { readInstalledUnityPackages } from "../src/extensions/project-resolver.js";
import { validateExtensionManifestContracts } from "../src/contracts/command-contract.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { checkToolExtensionAvailability, extensionAvailabilityFlags, freezeExtensionAvailability } from "../src/extension-availability.js";
import { builtinToolMetadata, builtinToolGroups, EXTENSION_GROUPS } from "../src/tools/metadata/index.js";
import { builtinExtensionMetadata } from "../src/extensions/metadata/index.js";
import { builtinExtensionToolContributions, findBuiltinExtensionToolContribution, requiredPackagesForContribution } from "../src/extensions/tool-manifest.js";
import { makeLiveTool, probuilderDeleteFacesTool, probuilderPingTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";
import { makeRegistry, makeRegistrySession, registryIdentity, writeRegistry } from "./helpers/instance-registry-v3.js";
import type { ToolMetadata } from "../src/tools/metadata/index.js";

test("URP and HDRP Volume capabilities use one action-free source per tool", async () => {
  for (const pipeline of ["urp", "hdrp"] as const) {
    const directory = new URL(`../../extensions/${pipeline}/unitypkg/Editor/${pipeline === "urp" ? "Urp" : "Hdrp"}/`, import.meta.url);
    const expected = ["list", "info", "create", "add-effect", "set"];
    for (const action of expected) {
      const source = await readFile(new URL(`volume-${action}.cs`, directory), "utf8");
      assert.equal((source.match(/:\s*IUnityCliTool</g) ?? []).length, 1);
      assert.match(source, new RegExp(`Name = "${pipeline}\\.volume\\.${action}"`));
      assert.doesNotMatch(source, /public string Action/);
    }
  }
});

test("URP/HDRP Volume profile lookup avoids stale runtime clones", async () => {
  for (const [pipeline, folder] of [["urp", "Urp"], ["hdrp", "Hdrp"]] as const) {
    const source = await readFile(new URL(`../../extensions/${pipeline}/unitypkg/Editor/${folder}/${folder}ToolSupport.cs`, import.meta.url), "utf8");
    const mutationLookup = source.slice(source.indexOf("public static UnityEngine.Object GetVolumeProfile"), source.indexOf("internal static UnityEngine.Object GetProfileForPlanning"));
    assert.match(mutationLookup, /GetProperty\("profile"/i, `${pipeline} mutation must use an independent runtime profile`);
    assert.doesNotMatch(mutationLookup, /GetProperty\("sharedProfile"/, `${pipeline} mutation must not write the shared profile asset`);
  }
  const addEffect = await readFile(new URL("../../extensions/urp/unitypkg/Editor/Urp/volume-add-effect.cs", import.meta.url), "utf8");
  assert.match(addEffect, /Description = "Add Vignette"[\s\S]*EffectType = "Vignette"/);
});

test("VFX template example and resolver use an installed package asset path", async () => {
  const source = await readFile(new URL("../../extensions/vfx-graph/unitypkg/Editor/Vfx/template-create.cs", import.meta.url), "utf8");
  const support = await readFile(new URL("../../extensions/vfx-graph/unitypkg/Editor/Vfx/VfxToolSupport.cs", import.meta.url), "utf8");
  assert.match(source, /Template = "SimpleParticleSystem"/);
  assert.doesNotMatch(source, /Template = "SimpleParticle"[,}]/);
  assert.match(support, /return "Packages\/" \+ packageName \+ "\/" \+ relative/);
  assert.doesNotMatch(support, /return ToAssetPath\(file\)/);
});

test("render pipeline settings declare reflective output as explicit arbitrary JSON", async () => {
  for (const pipeline of ["hdrp", "urp"]) {
    const domain = pipeline === "hdrp" ? "Hdrp" : "Urp";
    for (const operation of ["get", "set"]) {
      const source = await readFile(new URL(`../../extensions/${pipeline}/unitypkg/Editor/${domain}/settings-${operation}.cs`, import.meta.url), "utf8");
      assert.match(source, /\[UnityCliAnyJson\]\s+public Dictionary<string, object> Settings/);
      assert.match(source, new RegExp(`Name = "${pipeline}\\.settings\\.${operation}"`));
    }
  }
});

const requestId = "test_p5t04_001";
const emptyRuntime: ToolsRuntime = {};

async function makeFakeProject(packages: Record<string, string> = {}): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), "unity_cli-p5t04-"));
  const pkgDir = join(dir, "Packages");
  await Promise.all([
    mkdir(pkgDir, { recursive: true }),
    mkdir(join(dir, "Assets"), { recursive: true }),
    mkdir(join(dir, "ProjectSettings"), { recursive: true }),
  ]);
  await Promise.all([
    writeFile(join(pkgDir, "manifest.json"), JSON.stringify({ dependencies: packages }, null, 2)),
    writeFile(join(dir, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n"),
  ]);
  const lockDeps: Record<string, { version: string }> = {};
  for (const [id, version] of Object.entries(packages)) {
    lockDeps[id] = { version };
  }
  await writeFile(join(pkgDir, "packages-lock.json"), JSON.stringify({ dependencies: lockDeps }, null, 2));
  return dir;
}

/** Tool 级 routing 只接受 instance；测试 registry 将该 instance 绑定到真实临时项目。 */
async function selectedProject(projectPath: string, projectTools: ToolMetadata[]): Promise<{ flags: Record<string, string>; runtime: ToolsRuntime }> {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-p5t04-registry-"));
  const identity = registryIdentity(projectPath);
  const session = await makeRegistrySession("http://127.0.0.1:65534");
  await writeRegistry(dataDir, makeRegistry(identity, [session]));
  return { flags: { instance: identity.instanceId }, runtime: { dataDir, projectTools } };
}

test("concurrent extension installs preserve every manifest entry", async () => {
  const projectPath = await makeFakeProject({
    "com.unity.render-pipelines.high-definition": "14.0.11",
    "com.unity.render-pipelines.universal": "14.0.11"
  });
  const registry = new ExtensionRegistry();
  await Promise.all([
    installExtensionBundle(projectPath, registry.getExtension("hdrp")!.manifest),
    installExtensionBundle(projectPath, registry.getExtension("urp")!.manifest)
  ]);

  const manifest = await readExtensionInstallManifest(projectPath);
  assert.deepEqual(manifest.extensions.map((entry) => entry.extensionId).sort(), ["hdrp", "urp"]);
  const states = await checkExtensionBundles(projectPath);
  assert.equal(states.find((state) => state.extensionId === "hdrp")?.installed, true);
  assert.equal(states.find((state) => state.extensionId === "urp")?.installed, true);
});

test("extension install manifest enforces the shared ID syntax and entry limit", async () => {
  const projectPath = await makeFakeProject();
  const packageRoot = join(projectPath, "Packages", "com.alpsckr.unitycli");
  const manifestPath = join(packageRoot, "unitycli.extensions.manifest.json");
  await mkdir(packageRoot, { recursive: true });
  const entry = (extensionId: string) => ({
    extensionId,
    version: "1.0.0",
    installedAt: "2026-01-01T00:00:00.000Z",
    sourceHash: "a".repeat(64),
    files: [],
    requiresUnityPackages: [],
    copyRoot: "extension-directory",
    layout: "directory",
    installMode: "copy-if-package-present",
  });

  await writeFile(manifestPath, JSON.stringify({ version: 1, extensions: [entry("unknown\n")] }));
  await assert.rejects(() => readExtensionInstallManifest(projectPath), { code: "E_EXTENSION_INSTALL_MANIFEST_INVALID" });
  await writeFile(manifestPath, JSON.stringify({ version: 1, extensions: Array.from({ length: 257 }, (_, index) => entry(`unknown-${index}`)) }));
  await assert.rejects(() => readExtensionInstallManifest(projectPath), { code: "E_EXTENSION_INSTALL_MANIFEST_INVALID" });
});

test("extension manifests stay visible without an offline extension metadata registry", () => {
  assert.ok(!builtinToolGroups().includes("probuilder"));
  assert.ok(EXTENSION_GROUPS.has("probuilder"));
  assert.equal(builtinExtensionMetadata().length, 5);
});

test("extension manifest contributions expose tool names and required packages", () => {
  const all = builtinExtensionToolContributions();
  assert.ok(all.some((tool) => tool.name === "probuilder.ping"));
  assert.ok(all.some((tool) => tool.name === "cinemachine.ping"));
  assert.ok(all.some((tool) => tool.name === "vfx.graph.info"));
  assert.ok(all.some((tool) => tool.name === "vfx.graph.create"));

  const probuilder = findBuiltinExtensionToolContribution("probuilder.ping");
  const vfxGraph = findBuiltinExtensionToolContribution("vfx.graph.info");
  assert.ok(probuilder);
  assert.ok(vfxGraph);
  assert.deepEqual(requiredPackagesForContribution(probuilder!.manifest, probuilder!.contribution), ["com.unity.probuilder"]);
  assert.deepEqual(requiredPackagesForContribution(vfxGraph!.manifest, vfxGraph!.contribution), ["com.unity.visualeffectgraph"]);
});

test("tools describe and selected Tool gate preserve extension dependency severity", async () => {
  const projectPath = await makeFakeProject();
  const selected = await selectedProject(projectPath, [probuilderPingTool()]);

  const describe = await handleToolsCommand("describe", ["probuilder.ping"], selected.flags, selected.runtime, requestId);
  assert.ok(describe.ok);
  const describeData = describe.data as Record<string, unknown>;
  assert.equal(describeData.available, false);
  assert.match(String(describeData.unavailableReason), /E_EXTENSION_DEPENDENCY_MISSING/);
  assert.ok((describeData.requiresPackages as string[]).includes("com.unity.probuilder"));

  const run = await handleToolsCommand("run", ["probuilder.ping"], { ...selected.flags, input: "{}" }, selected.runtime, `${requestId}_run`);
  assert.ok(!run.ok);
  assert.equal(run.error.code, "E_EXTENSION_DEPENDENCY_MISSING");
  assert.equal(run.error.transient, false);
  assert.equal(run.error.retryAfterMs, 0);
  assert.ok(run.error.nextActions.some((action) => action.includes("Unity Package Manager") || action.includes("Packages/manifest.json")));
});

test("tools describe requires a healthy physical extension installation", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.2.4" });
  const registry = new ExtensionRegistry();
  await installExtensionBundle(projectPath, registry.getExtension("probuilder")!.manifest);
  const selected = await selectedProject(projectPath, [probuilderPingTool()]);

  const result = await handleToolsCommand("describe", ["probuilder.ping"], selected.flags, selected.runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(Object.hasOwn(data, "available"), false);
  assert.equal(data.unavailableReason, undefined);
});

test("tools describe reports the exact expected and actual package profile", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.2.3" });
  const selected = await selectedProject(projectPath, [probuilderPingTool()]);

  const result = await handleToolsCommand("describe", ["probuilder.ping"], selected.flags, selected.runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.available, false);
  assert.match(String(data.unavailableReason), /com\.unity\.probuilder@5\.2\.4 \(actual 5\.2\.3\)/);
});

test("tools describe without --project reports physical state as unknown", async () => {
  const runtime: ToolsRuntime = { projectTools: [probuilderPingTool()] };
  const result = await handleToolsCommand("describe", ["probuilder.ping"], {}, runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "probuilder.ping");
  assert.equal(data.available, false);
  assert.match(String(data.unavailableReason), /E_EXTENSION_PHYSICAL_STATE_UNKNOWN/);
});

test("non-extension live-discovered descriptors are unaffected by package checks", async () => {
  const projectPath = await makeFakeProject();
  const selected = await selectedProject(projectPath, [sceneHierarchyTool()]);
  const result = await handleToolsCommand("describe", ["scene.hierarchy"], selected.flags, selected.runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(Object.hasOwn(data, "available"), false);
  assert.equal(data.unavailableReason, undefined);
});

test("checkToolExtensionAvailability works with constructed live descriptors", async () => {
  const missingProject = await makeFakeProject();
  const availableProject = await makeFakeProject({ "com.unity.probuilder": "5.2.4" });

  const unavailable = await checkToolExtensionAvailability(probuilderPingTool(), await freezeExtensionAvailability({ project: missingProject }, {}));
  const packageOnly = await checkToolExtensionAvailability(probuilderPingTool(), await freezeExtensionAvailability({ project: availableProject }, {}));
  assert.equal(packageOnly.errorCode, "E_EXTENSION_NOT_INSTALLED");
  assert.equal(unavailable.available, false);
  assert.equal(unavailable.extensionId, "probuilder");

  const registry = new ExtensionRegistry();
  await installExtensionBundle(availableProject, registry.getExtension("probuilder")!.manifest);
  const available = await checkToolExtensionAvailability(probuilderPingTool(), await freezeExtensionAvailability({ project: availableProject }, {}));
  assert.equal(available.available, true);
  assert.equal(available.extensionId, "probuilder");

  const plain = await checkToolExtensionAvailability(sceneHierarchyTool(), await freezeExtensionAvailability({ project: missingProject }, {}));
  assert.equal(plain.available, true);

  const unboundExtension = await checkToolExtensionAvailability(makeLiveTool({
    name: "fixture.unbound-extension",
    summary: "Probe one unbound extension descriptor.",
    description: "Probe one extension-scoped descriptor that is intentionally absent from the extension inventory.",
    source: "extension",
    group: "fixture",
  }), await freezeExtensionAvailability({ project: missingProject }, {}));
  assert.equal(unboundExtension.available, false);
  assert.equal(unboundExtension.errorCode, "E_EXTENSION_INVENTORY_MISMATCH");
});

test("group discovery freezes one snapshot and proves each distinct extension once", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.2.4" });
  const registry = new ExtensionRegistry();
  await installExtensionBundle(projectPath, registry.getExtension("probuilder")!.manifest);
  const selected = await selectedProject(projectPath, [probuilderPingTool(), probuilderDeleteFacesTool()]);
  const operations: Array<{ operation: string; extensionId?: string }> = [];
  setExtensionFilesystemOperationObserverForTest((operation, extensionId) => operations.push({ operation, extensionId }));
  try {
    const result = await handleToolsCommand("group", ["list", "probuilder"], selected.flags, selected.runtime, "req_extension_page_proof");
    assert.equal(result.ok, true, JSON.stringify(result));
    const installed = (await readExtensionInstallManifest(projectPath)).extensions.find((entry) => entry.extensionId === "probuilder")!;
    assert.equal(operations.filter((item) => item.operation === "snapshot").length, 1);
    assert.deepEqual(operations.filter((item) => item.operation === "target-proof").map((item) => item.extensionId), ["probuilder"]);
    assert.equal(operations.filter((item) => item.operation === "managed-file-hash").length, installed.files.length);
    assert.equal(operations.filter((item) => item.operation === "source-proof").length, 0);
    assert.equal(operations.filter((item) => item.operation === "source-file-hash").length, 0);
  } finally {
    setExtensionFilesystemOperationObserverForTest(undefined);
  }
});

test("ordinary extension execution does not trigger all-extension source hashing", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.2.4" });
  const registry = new ExtensionRegistry();
  await installExtensionBundle(projectPath, registry.getExtension("probuilder")!.manifest);
  const selected = await selectedProject(projectPath, [probuilderPingTool()]);
  selected.runtime.runtimeTools = [];
  selected.runtime.bridgeExecute = async (payload) => ({ ok: true, requestId: payload.requestId, data: { result: {} } });
  const operations: Array<{ operation: string; extensionId?: string }> = [];
  setExtensionFilesystemOperationObserverForTest((operation, extensionId) => operations.push({ operation, extensionId }));
  try {
    const result = await handleToolsCommand("run", ["probuilder.ping"], { ...selected.flags, input: "{}" }, selected.runtime, "req_extension_execute_proof");
    assert.equal(result.ok, true, JSON.stringify(result));
    const installed = (await readExtensionInstallManifest(projectPath)).extensions.find((entry) => entry.extensionId === "probuilder")!;
    assert.deepEqual(operations.filter((item) => item.operation === "target-proof").map((item) => item.extensionId), ["probuilder"]);
    assert.equal(operations.filter((item) => item.operation === "managed-file-hash").length, installed.files.length);
    assert.equal(operations.filter((item) => item.operation === "source-proof").length, 0);
    assert.equal(operations.filter((item) => item.operation === "source-file-hash").length, 0);
  } finally {
    setExtensionFilesystemOperationObserverForTest(undefined);
  }
});

test("extensions list stays shallow while doctor owns all-extension deep hashes", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.2.4" });
  const operations: Array<{ operation: string; extensionId?: string }> = [];
  setExtensionFilesystemOperationObserverForTest((operation, extensionId) => operations.push({ operation, extensionId }));
  try {
    const listed = await listExtensionBundles(projectPath);
    assert.ok(listed.every((item) => item.integrity === "shallow"));
    assert.equal(operations.filter((item) => item.operation === "source-proof").length, 0);
    assert.equal(operations.filter((item) => item.operation === "target-proof").length, 0);
    operations.length = 0;
    const diagnosed = await checkExtensionBundles(projectPath, { absoluteDeadlineMs: Date.now() + 30_000 });
    assert.ok(diagnosed.every((item) => item.integrity === "full"));
    assert.equal(operations.filter((item) => item.operation === "source-proof").length, registryExtensionCount());
    assert.equal(operations.filter((item) => item.operation === "target-proof").length, registryExtensionCount());
    assert.ok(operations.filter((item) => item.operation === "source-file-hash").length > 0);
    assert.equal(operations.filter((item) => item.operation === "managed-file-hash").length, 0);
  } finally {
    setExtensionFilesystemOperationObserverForTest(undefined);
  }
});

test("deep extension inspection stops before traversing a large unknown tree after deadline", async () => {
  const projectPath = await makeFakeProject();
  const unknownRoot = join(projectPath, "Packages", "com.alpsckr.unitycli", "Extensions", "cinemachine", "unknown");
  await mkdir(unknownRoot, { recursive: true });
  await Promise.all(Array.from({ length: 512 }, (_, index) => writeFile(join(unknownRoot, `file-${index}.txt`), "unknown")));
  const operations: string[] = [];
  let now = 1_000;
  setExtensionFilesystemClockForTest(() => now);
  setExtensionFilesystemOperationObserverForTest((operation) => {
    operations.push(operation);
    if (operation === "unknown-tree-entry") now += 1;
  });
  try {
    await assert.rejects(() => checkExtensionBundles(projectPath, { absoluteDeadlineMs: 1_004 }), (error: unknown) =>
      error instanceof Error && (error as { code?: string }).code === "E_EXTENSION_SCAN_TIMEOUT");
    assert.equal(operations.filter((operation) => operation === "unknown-tree-entry").length, 4);
    assert.ok(operations.filter((operation) => operation === "unknown-tree-entry").length < 512);
  } finally {
    setExtensionFilesystemClockForTest(undefined);
    setExtensionFilesystemOperationObserverForTest(undefined);
  }
});

test("extension availability keeps explicit project and unresolved strict instance remains unknown", async () => {
  const explicitProject = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-extension-flags-"));

  const explicit = await extensionAvailabilityFlags({ project: explicitProject, instance: `unity-${"f".repeat(64)}` }, { dataDir });
  assert.equal(explicit.project, explicitProject);

  const unresolvedFlags = await extensionAvailabilityFlags({ instance: `unity-${"f".repeat(64)}` }, { dataDir });
  const unresolved = await checkToolExtensionAvailability(probuilderPingTool(), await freezeExtensionAvailability(unresolvedFlags, { dataDir }));
  assert.equal(unresolved.errorCode, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN");
});

function registryExtensionCount(): number {
  return new ExtensionRegistry().extensionIds().length;
}

test("probuilder.scene is exposed only as a Tool contribution", () => {
  const probuilder = BUILTIN_EXTENSION_MANIFESTS.find((manifest) => manifest.id === "probuilder");
  assert.ok(probuilder);
  assert.ok(probuilder.contributions.tools?.some((entry) => entry.name === "probuilder.scene"));
  assert.equal(Object.hasOwn(probuilder.contributions, "contexts"), false);
  assert.equal(findBuiltinExtensionToolContribution("probuilder.scene")?.extensionId, "probuilder");
});

test("doctor treats uninstalled optional extensions as warnings", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-optional-"));
  const projectPath = await makeFakeProject();

  const doctorRuntime: DoctorRuntime = { dataDir };
  const doctor = await handleDoctorCommand({ project: projectPath }, doctorRuntime, "req_doctor_optional_ext");
  assert.ok(doctor.ok);
  const checks = (doctor.data as { checks: Array<{ name: string; status: string; details?: Record<string, unknown> }> }).checks;
  const extCheck = checks.find((check) => check.name === "extensions");
  assert.equal(extCheck?.status, "warn");
  assert.equal(extCheck?.details?.optionalUninstalled, 5);
  assert.equal((extCheck?.details?.optional as unknown[]).length, 5);
  assert.equal(extCheck?.details?.optionalTruncated, false);
  assert.equal(Object.hasOwn(extCheck?.details ?? {}, "code"), false);
});

test("doctor fails unknown installed manifest entries with bounded stable details", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-unknown-installed-"));
  const projectPath = await makeFakeProject();
  const bridgePackage = join(projectPath, "Packages", "com.alpsckr.unitycli");
  await mkdir(bridgePackage, { recursive: true });
  const extensions = Array.from({ length: 40 }, (_, index) => ({
    extensionId: `unknown-${index}`,
    version: "1.0.0",
    installedAt: "2026-01-01T00:00:00.000Z",
    sourceHash: "a".repeat(64),
    files: [],
    requiresUnityPackages: [],
    copyRoot: "extension-directory",
    layout: "directory",
    installMode: "copy-if-package-present",
  }));
  await writeFile(join(bridgePackage, "unitycli.extensions.manifest.json"), JSON.stringify({ version: 1, extensions }));

  const doctor = await handleDoctorCommand({ project: projectPath }, { dataDir }, "req_doctor_unknown_installed");
  assert.ok(doctor.ok);
  const extCheck = (doctor.data as { checks: Array<{ name: string; status: string; details?: Record<string, unknown> }> }).checks
    .find((check) => check.name === "extensions");
  assert.equal(extCheck?.status, "fail");
  assert.equal(extCheck?.details?.code, "E_EXTENSION_UNKNOWN_INSTALLED");
  assert.equal(extCheck?.details?.unknownInstalledCount, 40);
  assert.equal((extCheck?.details?.unknownInstalled as unknown[]).length, 32);
  assert.equal(extCheck?.details?.unknownInstalledTruncated, true);
  assert.equal(extCheck?.details?.unknownEnabledCount, 0);
  assert.ok((extCheck?.details?.nextActions as string[]).some((action) => action.includes("manifest")));
});

test("doctor reports unknown enabled state independently with bounded samples", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-unknown-enabled-"));
  const projectPath = await makeFakeProject();
  const bridgePackage = join(projectPath, "Packages", "com.alpsckr.unitycli");
  await mkdir(bridgePackage, { recursive: true });
  const extensions = Array.from({ length: 40 }, (_, index) => `unknown-${index}`);
  await writeFile(join(bridgePackage, "extensions.json"), JSON.stringify({ extensions }));

  const doctor = await handleDoctorCommand({ project: projectPath }, { dataDir }, "req_doctor_unknown_enabled");
  assert.ok(doctor.ok);
  const extCheck = (doctor.data as { checks: Array<{ name: string; status: string; details?: Record<string, unknown> }> }).checks
    .find((check) => check.name === "extensions");
  assert.equal(extCheck?.status, "fail");
  assert.equal(extCheck?.details?.code, "E_EXTENSION_STATE_INVALID");
  assert.equal(extCheck?.details?.unknownInstalledCount, 0);
  assert.equal(extCheck?.details?.unknownEnabledCount, 40);
  assert.equal((extCheck?.details?.unknownEnabled as unknown[]).length, 32);
  assert.equal(extCheck?.details?.unknownEnabledTruncated, true);
  assert.ok((extCheck?.details?.nextActions as string[]).some((action) => action.includes("extensions.json")));
});

test("doctor fails installed or enabled broken extensions with a stable dependency code", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-broken-"));
  const projectPath = await makeFakeProject();
  const bridgePackage = join(projectPath, "Packages", "com.alpsckr.unitycli");
  await mkdir(bridgePackage, { recursive: true });
  await writeFile(join(bridgePackage, "extensions.json"), JSON.stringify({ extensions: ["probuilder"] }));

  const doctor = await handleDoctorCommand({ project: projectPath }, { dataDir }, "req_doctor_broken_ext");
  assert.ok(doctor.ok);
  const extCheck = (doctor.data as { checks: Array<{ name: string; status: string; details?: Record<string, unknown> }> }).checks
    .find((check) => check.name === "extensions");
  assert.equal(extCheck?.status, "fail");
  assert.equal(extCheck?.details?.code, "E_EXTENSION_DEPENDENCY_MISSING");
  assert.equal((extCheck?.details?.broken as unknown[]).length, 1);
});

test("validateExtensionManifestContracts validates manifests against bridge routing without offline extension descriptors", () => {
  assert.doesNotThrow(() => validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry, []));
  assert.throws(
    () => validateExtensionManifestContracts([
      {
        ...BUILTIN_EXTENSION_MANIFESTS[0],
        contributions: {
          ...BUILTIN_EXTENSION_MANIFESTS[0].contributions,
          tools: [{ name: "probuilder.missing-tool" }]
        }
      }
    ], bridgeCommandRegistry, []),
    /missing from the contract/
  );
});

test("URP, HDRP, Cinemachine, and VFX extension bridge specs route through project-tool.run", () => {
  const expected = [
    ["urp", "feature-list", "urp.feature.list"],
    ["urp", "feature-add", "urp.feature.add"],
    ["urp", "feature-toggle", "urp.feature.toggle"],
    ["urp", "feature-configure", "urp.feature.configure"],
    ["urp", "feature-remove", "urp.feature.remove"],
    ["urp", "volume-list", "urp.volume.list"],
    ["urp", "volume-create", "urp.volume.create"],
    ["urp", "volume-info", "urp.volume.info"],
    ["urp", "volume-add-effect", "urp.volume.add-effect"],
    ["urp", "volume-set", "urp.volume.set"],
    ["urp", "settings-get", "urp.settings.get"],
    ["urp", "settings-set", "urp.settings.set"],
    ["hdrp", "volume-list", "hdrp.volume.list"],
    ["hdrp", "volume-create", "hdrp.volume.create"],
    ["hdrp", "volume-info", "hdrp.volume.info"],
    ["hdrp", "volume-add-effect", "hdrp.volume.add-effect"],
    ["hdrp", "volume-set", "hdrp.volume.set"],
    ["hdrp", "settings-get", "hdrp.settings.get"],
    ["hdrp", "settings-set", "hdrp.settings.set"],
    ["cinemachine", "ping", "cinemachine.ping"],
    ["vfx", "graph.info", "vfx.graph.info"],
    ["vfx", "graph.create", "vfx.graph.create"],
    ["vfx", "effect.add", "vfx.effect.add"],
    ["vfx", "trail.create", "vfx.trail.create"],
    ["vfx", "trail.set", "vfx.trail.set"]
  ] as const;
  for (const [group, command, toolName] of expected) {
    const spec = bridgeCommandRegistry.lookup(group, command);
    assert.equal(spec?.bridgeCommand, "project-tool.run", `${toolName}: bridge command`);
    assert.equal(spec?.toolName, toolName, `${toolName}: tool name`);
  }
});

test("extension registry helpers still expose contribution counts", async () => {
  const registry = new ExtensionRegistry();
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });
  const installedPackages = await readInstalledUnityPackages(projectPath);

  assert.equal(registry.extensionIds().length, 5);
  assert.equal(findBuiltinExtensionToolContribution("cinemachine.ping")?.extensionId, "cinemachine");
  assert.equal(findBuiltinExtensionToolContribution("urp.feature.list")?.extensionId, "urp");
  assert.equal(findBuiltinExtensionToolContribution("hdrp.volume.list")?.extensionId, "hdrp");
  const probuilderMetadata = builtinExtensionMetadata().find((entry) => entry.id === "probuilder");
  assert.ok(probuilderMetadata);
  assert.ok((probuilderMetadata?.providesTools ?? []).includes("probuilder.ping"));
  assert.ok((probuilderMetadata?.providesTools ?? []).includes("probuilder.scene"));
  assert.ok(builtinToolMetadata().every((tool) => tool.source !== "extension"));

  const probuilder = registry.getExtension("probuilder");
  assert.ok(probuilder);
  assert.equal(probuilder!.manifest.contributions.tools?.length, 37);
  assert.equal(probuilderDeleteFacesTool().source, "extension");
  assert.equal(probuilderDeleteFacesTool().destructiveHint, true);
  assert.equal(installedPackages.has("com.unity.probuilder"), true);
});
