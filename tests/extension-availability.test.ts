import assert from "node:assert/strict";
import test from "node:test";
import { mkdtemp, writeFile, mkdir } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";

import { handleToolsCommand, type ToolsRuntime } from "../src/tools.js";
import { handleDoctorCommand, type DoctorRuntime } from "../src/doctor.js";
import { handleCapabilitiesCommand } from "../src/capabilities.js";
import { ExtensionRegistry, BUILTIN_EXTENSION_MANIFESTS } from "../src/extensions/index.js";
import { readInstalledUnityPackages } from "../src/extensions/project-resolver.js";
import { validateExtensionManifestContracts } from "../src/contracts/command-contract.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { checkToolExtensionAvailability, checkContextExtensionAvailability } from "../src/extension-availability.js";
import { builtinToolMetadata, builtinToolGroups, extensionToolMetadata, EXTENSION_GROUPS } from "../src/tools/metadata/index.js";
import { builtinExtensionMetadata } from "../src/extensions/metadata/index.js";
import { builtinExtensionToolContributions, findBuiltinExtensionToolContribution, requiredPackagesForContribution } from "../src/extensions/tool-manifest.js";
import { probuilderDeleteFacesTool, probuilderPingTool, sceneHierarchyTool } from "./helpers/live-tool-fixtures.js";

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

test("offline extension tool metadata is intentionally empty while extension manifests stay visible", () => {
  assert.equal(extensionToolMetadata().length, 0);
  assert.ok(!builtinToolGroups().includes("probuilder"));
  assert.ok(EXTENSION_GROUPS.has("probuilder"));
  assert.equal(builtinExtensionMetadata().length, 5);
});

test("extension manifest contributions expose tool names and required packages", () => {
  const all = builtinExtensionToolContributions();
  assert.ok(all.some((tool) => tool.name === "probuilder.ping"));
  assert.ok(all.some((tool) => tool.name === "cinemachine.ping"));
  assert.ok(all.some((tool) => tool.name === "vfx.graph"));

  const probuilder = findBuiltinExtensionToolContribution("probuilder.ping");
  const vfxGraph = findBuiltinExtensionToolContribution("vfx.graph");
  assert.ok(probuilder);
  assert.ok(vfxGraph);
  assert.deepEqual(requiredPackagesForContribution(probuilder!.manifest, probuilder!.contribution), ["com.unity.probuilder"]);
  assert.deepEqual(requiredPackagesForContribution(vfxGraph!.manifest, vfxGraph!.contribution), ["com.unity.visualeffectgraph"]);
});

test("tools describe resolves extension availability against live-discovered extension descriptors", async () => {
  const projectPath = await makeFakeProject();
  const runtime: ToolsRuntime = {
    projectTools: [probuilderPingTool()],
    bridgeExecute: async () => ({ ok: true, data: { installed: true } })
  };

  const describe = await handleToolsCommand("describe", ["probuilder.ping"], { project: projectPath }, runtime, requestId);
  assert.equal(describe.ok, true);
  const describeData = describe.data as Record<string, unknown>;
  assert.equal(describeData.extensionAvailability, "unavailable");
  assert.equal(describeData.extensionId, "probuilder");
  assert.ok((describeData.missingPackageIds as string[]).includes("com.unity.probuilder"));
});

test("tools describe reports extension available when required Unity package is installed", async () => {
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });
  const runtime: ToolsRuntime = { projectTools: [probuilderPingTool()] };

  const result = await handleToolsCommand("describe", ["probuilder.ping"], { project: projectPath }, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, "available");
  assert.equal(data.extensionId, "probuilder");
  assert.equal(data.missingPackageIds, undefined);
});

test("tools describe without --project does not block extension descriptors", async () => {
  const runtime: ToolsRuntime = { projectTools: [probuilderPingTool()] };
  const result = await handleToolsCommand("describe", ["probuilder.ping"], {}, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.name, "probuilder.ping");
  assert.equal(data.extensionAvailability, "available");
});

test("non-extension live-discovered descriptors are unaffected by package checks", async () => {
  const projectPath = await makeFakeProject();
  const runtime: ToolsRuntime = { projectTools: [sceneHierarchyTool()] };
  const result = await handleToolsCommand("describe", ["scene.hierarchy"], { project: projectPath }, runtime, requestId);
  assert.equal(result.ok, true);
  const data = result.data as Record<string, unknown>;
  assert.equal(data.extensionAvailability, undefined);
  assert.equal(data.extensionId, undefined);
});

test("checkToolExtensionAvailability works with constructed live descriptors", async () => {
  const missingProject = await makeFakeProject();
  const availableProject = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });

  const unavailable = await checkToolExtensionAvailability(probuilderPingTool(), { project: missingProject });
  assert.equal(unavailable.available, false);
  assert.equal(unavailable.extensionId, "probuilder");

  const available = await checkToolExtensionAvailability(probuilderPingTool(), { project: availableProject });
  assert.equal(available.available, true);
  assert.equal(available.extensionId, "probuilder");

  const plain = await checkToolExtensionAvailability(sceneHierarchyTool(), { project: missingProject });
  assert.equal(plain.available, true);
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
  assert.equal(doctor.ok, true);
  const checks = (doctor.data as { checks: Array<{ name: string; status: string }> }).checks;
  const extCheck = checks.find((check) => check.name === "extension_availability");
  assert.ok(extCheck);
  assert.equal(extCheck!.status, "fail");

  const capabilities = await handleCapabilitiesCommand({ project: projectPath }, {}, requestId);
  assert.equal(capabilities.ok, true);
  const ext = (capabilities.data as Record<string, unknown>).extensions as Record<string, unknown>;
  assert.equal(ext.total, 5);
});

test("validateExtensionManifestContracts validates manifests against bridge routing without offline extension descriptors", () => {
  assert.doesNotThrow(() => validateExtensionManifestContracts(BUILTIN_EXTENSION_MANIFESTS, bridgeCommandRegistry, extensionToolMetadata()));
  assert.throws(
    () => validateExtensionManifestContracts([
      {
        ...BUILTIN_EXTENSION_MANIFESTS[0],
        contributions: {
          ...BUILTIN_EXTENSION_MANIFESTS[0].contributions,
          tools: [{ name: "probuilder.missing-tool" }]
        }
      }
    ], bridgeCommandRegistry, extensionToolMetadata()),
    /missing from the contract/
  );
});

test("extension registry helpers still expose contribution counts", async () => {
  const registry = new ExtensionRegistry();
  const projectPath = await makeFakeProject({ "com.unity.probuilder": "5.0.0" });
  const installedPackages = await readInstalledUnityPackages(projectPath);

  assert.equal(registry.extensionIds().length, 5);
  assert.equal(findBuiltinExtensionToolContribution("cinemachine.ping")?.extensionId, "cinemachine");
  assert.equal(findBuiltinExtensionToolContribution("urp.feature")?.extensionId, "urp");
  assert.equal(findBuiltinExtensionToolContribution("hdrp.volume")?.extensionId, "hdrp");
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
