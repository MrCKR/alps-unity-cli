import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile } from "node:fs/promises";

const LOCAL_PACKAGE_COMMANDS = ["package.bridge-remove", "package.deploy", "package.info", "package.install", "package.list", "package.registry", "package.remove", "package.resolve", "package.restore", "package.scoped-registry", "package.search", "package.status"];

/** Package 域契约测试：TS 本地 package.* 继续离线可见；package.embed 改为 bridge-only。 */

test("package group exposes the 12 TS-local package tools", () => {
  const tools = filterToolsByGroup("package").map((tool) => tool.name).sort();
  assert.deepEqual(tools, [...LOCAL_PACKAGE_COMMANDS].sort());
  assert.equal(findToolByName("package.embed"), undefined, "package.embed should require bridge discovery");
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
  assert.match(install.description, /explicit version/);
  assert.match(install.description, /downgrade/);
  assert.match(JSON.stringify(install.outputSchema), /changed/);
  assert.match(JSON.stringify(install.examples), /3\.0\.7/);

  const remove = findToolByName("package.remove")!;
  assert.equal(remove.destructiveHint, true);
  assert.equal(remove.supportsDryRun, true);
  assert.equal(remove.riskLevel, "high");
  assert.equal(remove.playModeSupport, "session-affecting");
  assert.match(remove.description, /force=true/);
  assert.match(JSON.stringify(remove.inputSchema), /force/);
  assert.match(JSON.stringify(remove.outputSchema), /dependents/);

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
  assert.equal(deploy.supportStatus, "bridge-job");
  assert.equal(deploy.riskLevel, "high");
  assert.equal(deploy.playModeSupport, "session-affecting");
  assert.match(deploy.description, /package\.json/);
  assert.match(JSON.stringify(deploy.inputSchema), /source/);
  assert.match(JSON.stringify(deploy.outputSchema), /backupCreated/);

  const restore = findToolByName("package.restore")!;
  assert.equal(restore.destructiveHint, true);
  assert.equal(restore.supportsDryRun, true);
  assert.equal(restore.handler, "hybrid");
  assert.equal(restore.supportStatus, "bridge-job");
  assert.equal(restore.riskLevel, "high");
  assert.equal(restore.playModeSupport, "session-affecting");
  assert.match(restore.description, /package\.deploy/);
  assert.match(JSON.stringify(restore.inputSchema), /packageName/);
  assert.match(JSON.stringify(restore.outputSchema), /restoredFromBackup/);

  const resolve = findToolByName("package.resolve")!;
  assert.equal(resolve.longRunning, true);
  assert.equal(resolve.playModeSupport, "session-affecting");
});

test("package.embed is bridge-discovered and destructive in contract/spec/source", async () => {
  const contract = findCommandContract("package.embed");
  assert.ok(contract);
  assert.equal(contract?.handler_type, "bridge");
  assert.equal(contract?.destructive, true);

  const spec = bridgeCommandRegistry.lookup("package", "embed");
  assert.equal(spec?.bridgeCommand, "package.embed");

  const bridgeSpecSource = await readFile(new URL("../src/bridge/domains/package.js", import.meta.url), "utf8");
  const source = await readFile(new URL("../unitypkg/Editor/Tools/editor/action-tools.cs", import.meta.url), "utf8");
  assert.match(bridgeSpecSource, /packageEmbedArgs[\s\S]*packageId/);
  assert.match(bridgeSpecSource, /dryRun: boolFlag\(flags, "dry-run"\) \?\? boolField\(flags, "dryRun"\)/);
  assert.match(source, /class PackageEmbedTool : IUnityCliTool/);
  assert.match(source, /PackageClient\.Embed is not available/);
  assert.match(source, /PlannedEffects = new List<EditorPlannedEffect> \{ new EditorPlannedEffect \{ Type = "package\.embed"/);
});

test("contract entries exist for all package tools", () => {
  for (const name of [...LOCAL_PACKAGE_COMMANDS, "package.embed"]) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
  }
});

test("package scoped registry metadata documents remove guard", () => {
  const scoped = findToolByName("package.scoped-registry")!;
  assert.equal(scoped.handler, "offline");
  assert.match(scoped.description, /force=true/);
  assert.match(scoped.description, /--dry-run/);
  assert.match(JSON.stringify(scoped.inputSchema), /force/);
  assert.match(JSON.stringify(scoped.outputSchema), /matchedPackages/);
});

test("package C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const pm = await readFile(new URL("../src/package-manager.js", import.meta.url), "utf8");
  const cliPkg = await readFile(new URL("../src/cli-commands/package.js", import.meta.url), "utf8");
  assert.equal(pm.includes("unity_cli tools run package"), false);
  assert.equal(cliPkg.includes("unity_cli tools run package"), false);
  assert.match(pm, /tools run package\.list/);
  assert.match(cliPkg, /tools run package\.list/);
});

test("package.resolve rejects non-Unity project paths before skipped success", async () => {
  const source = await readFile(new URL("../src/cli-commands/package.js", import.meta.url), "utf8");
  const block = source.slice(source.indexOf("async function maybeResolvePackage"));
  assert.match(block, /join\(projectPath, "Packages", "manifest\.json"\)/);
  assert.match(block, /MANIFEST_NOT_FOUND/);
  assert.match(block, /blocked_missing_manifest/);
});

test("package.embed bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/package.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "package"\)/);
  assert.match(spec, /boolField\(flags, "dryRun"\)/);
});
