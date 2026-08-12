import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { resolveProjectIdentity } from "../src/project-identity.js";
import "../src/tools/.generated-registry.js";
import "../src/tools/.generated-schemas.js";
import { dispatchTool } from "../src/tools/dispatcher.js";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { createUnityProject } from "./helpers/instance-registry-v2.js";

const LOCAL_PACKAGE_COMMANDS = ["package.bridge-remove", "package.deploy", "package.embed", "package.info", "package.install", "package.list", "package.registry.clear", "package.registry.get", "package.registry.set", "package.remove", "package.resolve", "package.restore", "package.scoped-registry.add", "package.scoped-registry.list", "package.scoped-registry.remove", "package.search", "package.status"];

/** Package 域契约测试：TS 本地 package.* 保持离线可见。 */

test("package group exposes the 17 TS-local package tools", () => {
  const tools = filterToolsByGroup("package").map((tool) => tool.name).sort();
  assert.deepEqual(tools, [...LOCAL_PACKAGE_COMMANDS].sort());
  assert.equal(findToolByName("package.embed")?.handler, "hybrid");
});

test("core Unity package declares its uGUI compile dependency", async () => {
  const packageManifest = JSON.parse(await readFile(new URL("../unitypkg/package.json", import.meta.url), "utf8")) as { dependencies?: Record<string, string> };
  const assembly = JSON.parse(await readFile(new URL("../unitypkg/UnityCli.Editor.asmdef", import.meta.url), "utf8")) as { references?: string[] };
  assert.equal(packageManifest.dependencies?.["com.unity.ugui"], "1.0.0");
  assert.ok(assembly.references?.includes("Unity.ugui"));
});

test("package read tools are offline/non-destructive/supported", () => {
  for (const name of ["package.list", "package.status", "package.info", "package.search"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false);
    assert.equal(tool.handler, "offline");
    assert.equal(tool.playModeSupport, "supported");
    assert.ok(tool.examples?.length);
  }
});

test("package install/remove/resolve expose play mode + risk metadata", () => {
  const install = findToolByName("package.install")!;
  assert.equal(install.playModeSupport, "session-affecting");
  assert.equal(install.destructiveHint, true);
  assert.equal(install.riskLevel, "high");
  assert.ok(install.sideEffects?.includes("manifest-change"));
  assert.equal(install.supportStatus, "live-bridge");
  assert.equal(install.timeoutMs, 120_000);
  assert.doesNotMatch(JSON.stringify(install.inputSchema), /project|unity/i);
  assert.doesNotMatch(JSON.stringify(install.examples), /--job|batchmode|--unity/);
  assert.match(JSON.stringify(install.outputSchema), /manifestHash/);
  assert.match(JSON.stringify(install.outputSchema), /files/);
  assert.match(JSON.stringify(install.outputSchema), /resolve/);
  assert.match(JSON.stringify(install.examples), /3\.0\.7/);

  const remove = findToolByName("package.remove")!;
  assert.equal(remove.destructiveHint, true);
  assert.equal(remove.supportsDryRun, true);
  assert.equal(remove.riskLevel, "high");
  assert.equal(remove.playModeSupport, "session-affecting");
  assert.equal(remove.supportStatus, "live-bridge");
  assert.equal(remove.timeoutMs, 120_000);
  assert.match(JSON.stringify(remove.inputSchema), /force/);
  assert.doesNotMatch(JSON.stringify(remove.inputSchema), /project|unity/i);
  assert.match(JSON.stringify(remove.outputSchema), /manifestHash/);
  assert.match(JSON.stringify(remove.outputSchema), /resolve/);

  const bridgeRemove = findToolByName("package.bridge-remove")!;
  assert.equal(bridgeRemove.destructiveHint, true);
  assert.equal(bridgeRemove.supportsDryRun, true);
  assert.equal(bridgeRemove.handler, "offline");
  assert.equal(bridgeRemove.riskLevel, "high");
  assert.equal(bridgeRemove.playModeSupport, "session-affecting");
  assert.match(bridgeRemove.description, /Packages\/com\.unity_cli/);

  const deploy = findToolByName("package.deploy")!;
  assert.equal(deploy.destructiveHint, true);
  assert.equal(deploy.supportsDryRun, true);
  assert.equal(deploy.handler, "hybrid");
  assert.equal(deploy.supportStatus, "live-bridge");
  assert.equal(deploy.timeoutMs, 120_000);
  assert.doesNotMatch(JSON.stringify(deploy.inputSchema), /project/i);
  assert.equal(deploy.riskLevel, "high");
  assert.equal(deploy.playModeSupport, "session-affecting");
  assert.match(deploy.description, /package\.json/);
  assert.match(JSON.stringify(deploy.inputSchema), /source/);
  assert.match(JSON.stringify(deploy.outputSchema), /backupCreated/);
  assert.doesNotMatch(JSON.stringify(deploy.outputSchema), /projectPath/);

  const restore = findToolByName("package.restore")!;
  assert.equal(restore.destructiveHint, true);
  assert.equal(restore.supportsDryRun, true);
  assert.equal(restore.handler, "hybrid");
  assert.equal(restore.supportStatus, "live-bridge");
  assert.equal(restore.timeoutMs, 120_000);
  assert.doesNotMatch(JSON.stringify(restore.inputSchema), /project/i);
  assert.equal(restore.riskLevel, "high");
  assert.equal(restore.playModeSupport, "session-affecting");
  assert.match(restore.description, /package\.deploy/);
  assert.match(JSON.stringify(restore.inputSchema), /packageName/);
  assert.match(JSON.stringify(restore.outputSchema), /restoredFromBackup/);
  assert.doesNotMatch(JSON.stringify(restore.outputSchema), /projectPath/);

  const resolve = findToolByName("package.resolve")!;
  assert.equal(resolve.playModeSupport, "session-affecting");
  assert.equal(resolve.supportStatus, "live-bridge");
  assert.equal(resolve.timeoutMs, 120_000);
  assert.doesNotMatch(JSON.stringify(resolve.inputSchema), /project|unity/i);
  assert.doesNotMatch(JSON.stringify(resolve.examples), /--job|batchmode|--unity/);
});

test("package.embed is TS-local and destructive with a private reload coordinator", async () => {
  const contract = findCommandContract("package.embed");
  assert.ok(contract);
  assert.equal(contract?.handler_type, "hybrid");
  assert.equal(contract?.destructive, true);
  assert.equal(bridgeCommandRegistry.lookup("package", "embed"), undefined);

  const source = await readFile(new URL("../src/tools/package/embed.js", import.meta.url), "utf8");
  const coordinator = await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliPackageEmbedCoordinator.cs", import.meta.url), "utf8");
  assert.match(source, /internal\.package\.embed\.start/);
  assert.match(source, /internal\.package\.embed\.status/);
  assert.match(coordinator, /PackageClient\.Embed is not available/);
  assert.match(coordinator, /PackageInfo\.FindForAssetPath/);
  assert.match(coordinator, /PackageSource\.Embedded/);
  assert.match(coordinator, /waiting_completed/);
  assert.match(coordinator, /waiting_stable/);
  const embedFenceIndex = coordinator.indexOf('CrossEffectFence(root, "package-embed", "embed_started")');
  const embedEffectIndex = coordinator.indexOf("method.Invoke");
  assert.ok(embedFenceIndex >= 0 && embedFenceIndex < embedEffectIndex, "package.embed must durably cross its exact effect fence before Embed can reload the domain.");
  assert.match(coordinator, /EmbeddedPath = actual/);
});

test("contract entries exist for all package tools", () => {
  for (const name of LOCAL_PACKAGE_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
  }
});

test("package registry tools expose single-action metadata and security", () => {
  for (const name of ["package.registry.get", "package.scoped-registry.list"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.handler, "offline");
    assert.equal(tool.destructiveHint, false);
    assert.equal(tool.supportsDryRun, undefined);
  }
  for (const name of ["package.registry.set", "package.registry.clear", "package.scoped-registry.add", "package.scoped-registry.remove"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.handler, "offline");
    assert.equal(tool.destructiveHint, true);
    assert.equal(tool.supportsDryRun, true);
    assert.doesNotMatch(JSON.stringify(tool.inputSchema), /action/);
  }
  const remove = findToolByName("package.scoped-registry.remove")!;
  assert.match(remove.description, /force=true/);
  assert.match(JSON.stringify(remove.inputSchema), /force/);
  assert.match(JSON.stringify(remove.outputSchema), /matchedPackages/);
});

test("local package manifest mutations reject an input project outside the selected instance before journal acceptance", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-cli-package-target-binding-"));
  const projectA = join(root, "ProjectA");
  const projectB = join(root, "ProjectB");
  const dataDir = join(root, "data");
  await Promise.all([createUnityProject(projectA), createUnityProject(projectB)]);
  const manifestPath = join(projectB, "Packages", "manifest.json");
  const originalManifest = "{\n  \"dependencies\": {}\n}\n";
  await writeFile(manifestPath, originalManifest, "utf8");
  const instanceIdA = resolveProjectIdentity(projectA).instanceId;
  const cases = [
    ["package.registry.set", { project: projectB, url: "https://registry.example.test" }],
    ["package.registry.clear", { project: projectB }],
    ["package.scoped-registry.add", { project: projectB, name: "Acme", url: "https://registry.example.test", scope: "com.acme" }],
    ["package.scoped-registry.remove", { project: projectB, name: "Acme", force: true }],
  ] as const;
  try {
    for (const [name, input] of cases) {
      const requestId = `req_target_${name.replaceAll(".", "_")}`;
      const result = await dispatchTool(name, undefined, { input: JSON.stringify(input), instance: projectA, yes: true }, { dataDir }, requestId);
      assert.equal(result.ok, false, name);
      if (!result.ok) assert.equal(result.error.code, "E_INSTANCE_PROJECT_MISMATCH", name);
      assert.equal(await readFile(manifestPath, "utf8"), originalManifest, name);
    }
    await assert.rejects(() => stat(join(dataDir, "requests", instanceIdA)), { code: "ENOENT" });
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("package error recovery hints use current tools run forms", async () => {
  const pm = await readFile(new URL("../src/package-manager.js", import.meta.url), "utf8");
  const init = await readFile(new URL("../src/cli-commands/package.js", import.meta.url), "utf8");
  assert.match(pm, /tools run package\.list/);
  assert.doesNotMatch(init, /handlePackage|bridge\.package\.refresh|batchmode|--job/);
});

test("package live tools use one public root and an explicit PackageManager List fence", async () => {
  for (const name of ["resolve", "install", "remove", "deploy", "restore"]) {
    const source = await readFile(new URL(`../src/tools/package/${name}.js`, import.meta.url), "utf8");
    assert.doesNotMatch(source, /resolvePackageChanges|refreshPackageEditor|bridge\.package\.refresh|batchmode|--job|_package_resolve|ProvenPartialEffectError/);
    assert.match(source, /runLivePackageResolve/);
  }
  const coordinator = [
    await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliPackageResolveCoordinator.cs", import.meta.url), "utf8"),
    await readFile(new URL("../unitypkg/Editor/Infrastructure/UnityCliPackageResolveState.cs", import.meta.url), "utf8"),
  ].join("\n");
  assert.match(coordinator, /Client\.Resolve\(\)/);
  assert.match(coordinator, /Client\.List\(false, true\)/);
  assert.match(coordinator, /PackageInfo\.GetAllRegisteredPackages\(\)/);
  assert.match(coordinator, /request\.Status >= StatusCode\.Failure/);
  assert.match(coordinator, /files_committing/);
  assert.match(coordinator, /fileExecutorStartIdentity/);
  assert.match(coordinator, /FileExecutorAlive/);
  assert.match(coordinator, /internal\.package\.files\.claim/);
  assert.match(coordinator, /MarkPackageFileEffectStartedActive/);
  assert.match(coordinator, /list_succeeded/);
  assert.match(coordinator, /stableFrames/);
  assert.match(coordinator, /RefreshListAfterRegisteredSnapshotMoves/);
  const relistStart = coordinator.indexOf("private static bool RefreshListAfterRegisteredSnapshotMoves");
  const relistEnd = coordinator.indexOf("private static Dictionary<string, object> Observe", relistStart);
  const relist = coordinator.slice(relistStart, relistEnd);
  assert.match(relist, /registeredMismatchStableFrames/);
  // active state 的 closed-schema 必须登记 relist 阶段写入的 registeredMismatch 字段。
  assert.match(coordinator, /"observedPackageSnapshotHash", "registeredMismatchHash", "registeredMismatchStableFrames", "listRefreshAttempts"/);
  assert.match(coordinator, /UnityCliActiveOperation\.Load\(ActiveScope\)/);
  assert.match(coordinator, /UnityCliActiveOperation\.Save\(ActiveScope/);
  assert.match(coordinator, /UnityCliActiveOperation\.BeginExactTerminalCleanup\(ActiveScope/);
  assert.doesNotMatch(coordinator, /RequestJournal|E_REQUEST_OUTCOME_UNKNOWN|E_REQUEST_JOURNAL/);
  assert.match(relist, /StartList\(RequiredRoot\(root\.RequestId\), false\)/);
  assert.doesNotMatch(relist, /Client\.Resolve|DispatchResolve|runPackageFileMutationWorker/);
  // PlayMode 下 PackageManager.Resolve 无法完成，同步调用会永久死锁主线程；pump 必须跳帧而不是发起 Resolve。
  const dispatchStart = coordinator.indexOf("private static void DispatchResolve");
  const dispatchEnd = coordinator.indexOf("private static void StartList", dispatchStart);
  const dispatch = coordinator.slice(dispatchStart, dispatchEnd);
  assert.match(dispatch, /EditorApplication\.isPlayingOrWillChangePlaymode/);
  const resolveCall = dispatch.indexOf("Client.Resolve()");
  const playGuard = dispatch.indexOf("EditorApplication.isPlayingOrWillChangePlaymode");
  assert.ok(playGuard >= 0 && playGuard < resolveCall, "PlayMode guard must precede the only Client.Resolve() call.");
  assert.doesNotMatch(coordinator, /_package_resolve|resolveCallReturned|stable_ready|leaseRequestId/);
});

test("package file guard uses Windows PowerShell and C# hashes depth-first per directory", async () => {
  const [liveResolve, packageState] = await Promise.all([
    readFile("src/tools/package/live-resolve.ts", "utf8"),
    readFile("unitypkg/Editor/Infrastructure/UnityCliPackageResolveState.cs", "utf8"),
  ]);
  assert.match(liveResolve, /spawn\("powershell\.exe"[\s\S]*"-ExecutionPolicy", "Bypass"/);
  assert.doesNotMatch(liveResolve, /spawn\("pwsh"/);
  assert.match(packageState, /HashDirectoryEntries\(sha, root, root\)[\s\S]*GetFileSystemEntries\(directory\)\.OrderBy\(Path\.GetFileName, StringComparer\.Ordinal\)[\s\S]*if \(isDirectory\) HashDirectoryEntries/);
});

test("package.embed uses the private start/status coordinator", async () => {
  const source = await readFile(new URL("../src/tools/package/embed.js", import.meta.url), "utf8");
  assert.match(source, /internal\.package\.embed\.start/);
  assert.match(source, /rootEnvelope/);
});
