import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, writeFile, mkdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { handleToolsCommand, type ToolsRuntime } from "../src/tools/commands.js";
import { handleDoctorCommand, type DoctorRuntime } from "../src/doctor.js";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import { ExtensionRegistry, BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import { checkExtensionBundles, installExtensionBundle, readExtensionInstallManifest } from "../src/extensions/installer.js";
import { readInstalledUnityPackages } from "../src/extensions/project-resolver.js";
import { validateExtensionManifestContracts } from "../src/contracts/command-contract.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { checkToolExtensionAvailability, checkContextExtensionAvailability, extensionAvailabilityFlags } from "../src/extension-availability.js";
import { writeInstances, type UnityInstance } from "../src/instances.js";
import { builtinToolMetadata, builtinToolGroups, EXTENSION_GROUPS } from "../src/tools/metadata/index.js";
import { builtinExtensionMetadata } from "../src/extensions/metadata/index.js";
import { builtinExtensionToolContributions, findBuiltinExtensionToolContribution, requiredPackagesForContribution } from "../src/extensions/tool-manifest.js";
import { probuilderDeleteFacesTool, probuilderPingTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";

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
    assert.ok(source.indexOf('GetProperty("sharedProfile"') < source.indexOf('GetProperty("profile"'), `${pipeline} must prefer sharedProfile`);
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
  await mkdir(pkgDir, { recursive: true });
  await writeFile(join(pkgDir, "manifest.json"), JSON.stringify({ dependencies: packages }, null, 2));
  const lockDeps: Record<string, { version: string }> = {};
  for (const [id, version] of Object.entries(packages)) {
    lockDeps[id] = { version };
  }
  await writeFile(join(pkgDir, "packages-lock.json"), JSON.stringify({ dependencies: lockDeps }, null, 2));
  return dir;
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

test("tools describe resolves extension availability against live-discovered extension descriptors", async () => {
  const projectPath = await makeFakeProject();
  const runtime: ToolsRuntime = {
    projectTools: [probuilderPingTool()],
    bridgeExecute: async (payload) => ({ ok: true as const, requestId: payload.requestId, data: { installed: true } })
  };

  const describe = await handleToolsCommand("describe", ["probuilder.ping"], { project: projectPath }, runtime, requestId);
  assert.ok(describe.ok);
  const describeData = describe.data as Record<string, unknown>;
  assert.equal(describeData.extensionAvailability, "unavailable");
  assert.equal(describeData.extensionId, "probuilder");
  assert.ok((describeData.missingPackageIds as string[]).includes("com.unity.probuilder"));
});

test("tools describe requires a healthy physical extension installation", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });
  const registry = new ExtensionRegistry();
  await installExtensionBundle(projectPath, registry.getExtension("probuilder")!.manifest);
  const runtime: ToolsRuntime = { projectTools: [probuilderPingTool()] };

  const result = await handleToolsCommand("describe", ["probuilder.ping"], { project: projectPath }, runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "available");
  assert.equal(data.extensionId, "probuilder");
  assert.equal(data.missingPackageIds, undefined);
});

test("tools describe without --project reports physical state as unknown", async () => {
  const runtime: ToolsRuntime = { projectTools: [probuilderPingTool()] };
  const result = await handleToolsCommand("describe", ["probuilder.ping"], {}, runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "probuilder.ping");
  assert.equal(data.extensionAvailability, "unavailable");
  assert.equal(data.extensionAvailabilityError, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN");
});

test("non-extension live-discovered descriptors are unaffected by package checks", async () => {
  const projectPath = await makeFakeProject();
  const runtime: ToolsRuntime = { projectTools: [sceneHierarchyTool()] };
  const result = await handleToolsCommand("describe", ["scene.hierarchy"], { project: projectPath }, runtime, requestId);
  assert.ok(result.ok);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, undefined);
  assert.equal(data.extensionId, undefined);
});

test("checkToolExtensionAvailability works with constructed live descriptors", async () => {
  const missingProject = await makeFakeProject();
  const availableProject = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });

  const unavailable = await checkToolExtensionAvailability(probuilderPingTool(), { project: missingProject });
  const packageOnly = await checkToolExtensionAvailability(probuilderPingTool(), { project: availableProject });
  assert.equal(packageOnly.errorCode, "E_EXTENSION_NOT_INSTALLED");
  assert.equal(unavailable.available, false);
  assert.equal(unavailable.extensionId, "probuilder");

  const registry = new ExtensionRegistry();
  await installExtensionBundle(availableProject, registry.getExtension("probuilder")!.manifest);
  const available = await checkToolExtensionAvailability(probuilderPingTool(), { project: availableProject });
  assert.equal(available.available, true);
  assert.equal(available.extensionId, "probuilder");

  const plain = await checkToolExtensionAvailability(sceneHierarchyTool(), { project: missingProject });
  assert.equal(plain.available, true);
});

test("extension availability project inference keeps explicit projects and reports unresolved targets as unknown", async () => {
  const explicitProject = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-extension-flags-"));
  const staleAt = new Date(Date.now() - 60_000).toISOString();
  const stale: UnityInstance = {
    instanceId: "stale-target",
    projectPath: await makeFakeProject(),
    bridgeEndpoint: "http://127.0.0.1:1",
    status: "stale",
    registeredAt: staleAt,
    lastHeartbeatAt: staleAt
  };
  const unreachable: UnityInstance = {
    ...stale,
    instanceId: "unreachable-target",
    status: "unreachable",
    projectPath: await makeFakeProject()
  };
  await writeInstances(dataDir, [stale, unreachable]);

  const explicit = await extensionAvailabilityFlags({ project: explicitProject, target: "stale-target" }, { dataDir });
  assert.equal(explicit.project, explicitProject);

  const staleFlags = await extensionAvailabilityFlags({ target: "stale-target" }, { dataDir });
  const staleResult = await checkToolExtensionAvailability(probuilderPingTool(), staleFlags);
  assert.equal(staleResult.errorCode, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN");

  const unreachableFlags = await extensionAvailabilityFlags({ target: "unreachable-target" }, { dataDir });
  const unreachableResult = await checkToolExtensionAvailability(probuilderPingTool(), unreachableFlags);
  assert.equal(unreachableResult.errorCode, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN");

  const endpointFlags = await extensionAvailabilityFlags({ "bridge-endpoint": "http://127.0.0.1:9" }, { dataDir: await mkdtemp(join(tmpdir(), "unity_cli-extension-empty-")) });
  const endpointResult = await checkToolExtensionAvailability(probuilderPingTool(), endpointFlags);
  assert.equal(endpointResult.errorCode, "E_EXTENSION_PHYSICAL_STATE_UNKNOWN");
});

test("checkContextExtensionAvailability still resolves extension contexts from manifests", async () => {
  const projectPath = await makeFakeProject();
  const result = await checkContextExtensionAvailability("probuilder.scene", { project: projectPath });
  assert.equal(result.available, false);
  assert.equal(result.extensionId, "probuilder");
});

test("doctor and capabilities keep extension summaries available from manifests", async () => {
  const dataDir = await mkdtemp(join(tmpdir(), "unity_cli-doctor-p5t04-"));
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });

  const doctorRuntime: DoctorRuntime = { dataDir };
  const doctor = await handleDoctorCommand({ project: projectPath }, doctorRuntime, "req_doctor_ext");
  assert.ok(doctor.ok);
  const checks = (doctor.data as { checks: Array<{ name: string; status: string }> }).checks;
  const extCheck = checks.find((check) => check.name === "extension_availability");
  assert.ok(extCheck);
  assert.equal(extCheck!.status, "fail");

  const capabilities = await handleCapabilitiesCommand({ project: projectPath }, {}, requestId);
  assert.ok(capabilities.ok);
  const ext = (capabilities.data as Record<string, unknown>).extensions as Record<string, unknown>;
  assert.equal(ext.total, 5);
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
    ["cinemachine", "force-camera", "cinemachine.force-camera"],
    ["cinemachine", "release-override", "cinemachine.release-override"],
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
  assert.ok(builtinToolMetadata().every((tool) => tool.source !== "extension"));

  const probuilder = registry.getExtension("probuilder");
  assert.ok(probuilder);
  assert.equal(probuilder!.manifest.contributions.tools?.length, 36);
  assert.equal(probuilderDeleteFacesTool().source, "extension");
  assert.equal(probuilderDeleteFacesTool().destructiveHint, true);
  assert.equal(installedPackages.has("com.unity.probuilder"), true);
});
