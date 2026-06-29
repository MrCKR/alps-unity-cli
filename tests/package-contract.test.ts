import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile } from "node:fs/promises";

const PACKAGE_COMMANDS = ["package.list", "package.status", "package.info", "package.search", "package.install", "package.remove", "package.bridge-remove", "package.deploy", "package.restore", "package.resolve", "package.registry", "package.scoped-registry", "package.embed"];

/**
 * Package 域契约测试：list/status/info/search/install/remove/bridge-remove/resolve/registry/scoped-registry (offline/hybrid) + embed (bridge)。
 * 对齐 CoplayDev manage_packages，验证 metadata/bridge/contract 一致，错误恢复提示迁移到 `tools run` 形态。
 * C# RegisterCommand 守卫不适用：除 embed 外的 package.* 经 offline/hybrid handlePackage 路由（非 bridge RegisterCommand）。
 */

test("package group exposes the 13 package tools", () => {
  const tools = filterToolsByGroup("package").map((t) => t.name).sort();
  assert.deepEqual(tools, ["package.bridge-remove", "package.deploy", "package.embed", "package.info", "package.install", "package.list", "package.registry", "package.remove", "package.resolve", "package.restore", "package.scoped-registry", "package.search", "package.status"]);
});

test("package read tools are offline/non-destructive/supported", () => {
  for (const name of ["package.list", "package.status", "package.info", "package.search"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.handler, "offline", `${name} offline`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.examples?.length, `${name} examples`);
  }
});

test("package install/remove/resolve expose play mode + risk metadata", () => {
  const install = findToolByName("package.install")!;
  assert.equal(install.playModeSupport, "session-affecting");
  assert.equal(install.destructiveHint, true);
  assert.equal(install.riskLevel, "high");
  assert.ok(install.sideEffects?.includes("manifest-change"));
  assert.match(install.description, /explicit version/, "package.install should explain explicit version requirement");
  assert.match(install.description, /downgrade/, "package.install should explain downgrade protection");
  assert.match(JSON.stringify(install.outputSchema), /changed/, "package.install output should report changed");
  assert.match(JSON.stringify(install.examples), /3\.0\.7/, "package.install example should include a real SemVer version");

  const remove = findToolByName("package.remove")!;
  assert.equal(remove.destructiveHint, true);
  assert.equal(remove.supportsDryRun, true);
  assert.equal(remove.riskLevel, "high");
  assert.equal(remove.playModeSupport, "session-affecting");
  assert.match(remove.description, /force=true/, "package.remove should explain dependent-package force override");
  assert.match(JSON.stringify(remove.inputSchema), /force/, "package.remove schema should expose force");
  assert.match(JSON.stringify(remove.outputSchema), /dependents/, "package.remove output should report dependents");

  const bridgeRemove = findToolByName("package.bridge-remove")!;
  assert.equal(bridgeRemove.destructiveHint, true);
  assert.equal(bridgeRemove.supportsDryRun, true);
  assert.equal(bridgeRemove.handler, "offline");
  assert.equal(bridgeRemove.riskLevel, "high");
  assert.equal(bridgeRemove.playModeSupport, "session-affecting");
  assert.match(bridgeRemove.description, /Packages\/com\.unity_cli/, "package.bridge-remove should explain copied package deletion");

  const deploy = findToolByName("package.deploy")!;
  assert.equal(deploy.destructiveHint, true);
  assert.equal(deploy.supportsDryRun, true);
  assert.equal(deploy.handler, "offline");
  assert.equal(deploy.riskLevel, "high");
  assert.equal(deploy.playModeSupport, "session-affecting");
  assert.match(deploy.description, /package\.json/, "package.deploy should explain source package requirement");
  assert.match(JSON.stringify(deploy.inputSchema), /source/, "package.deploy schema should expose source");
  assert.match(JSON.stringify(deploy.outputSchema), /backupCreated/, "package.deploy output should report backup state");

  const restore = findToolByName("package.restore")!;
  assert.equal(restore.destructiveHint, true);
  assert.equal(restore.supportsDryRun, true);
  assert.equal(restore.handler, "offline");
  assert.equal(restore.riskLevel, "high");
  assert.equal(restore.playModeSupport, "session-affecting");
  assert.match(restore.description, /package\.deploy/, "package.restore should explain deploy metadata dependency");
  assert.match(JSON.stringify(restore.inputSchema), /packageName/, "package.restore schema should expose packageName");
  assert.match(JSON.stringify(restore.outputSchema), /restoredFromBackup/, "package.restore output should report restore mode");

  const resolve = findToolByName("package.resolve")!;
  assert.equal(resolve.longRunning, true);
  assert.equal(resolve.playModeSupport, "session-affecting");
});

test("package.embed is destructive bridge with session-affecting play mode", () => {
  const embed = findToolByName("package.embed")!;
  assert.equal(embed.destructiveHint, true);
  assert.equal(embed.supportsDryRun, true);
  assert.equal(embed.handler, "bridge");
  assert.equal(embed.playModeSupport, "session-affecting");
  assert.ok(embed.sideEffects?.includes("domain-reload"));
});

test("bridge spec resolves for package.embed", () => {
  const spec = bridgeCommandRegistry.lookup("package", "embed");
  assert.equal(spec?.bridgeCommand, "package.embed");
});

test("contract entries exist for all package tools", () => {
  for (const name of PACKAGE_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
  }
});

test("package scoped registry metadata documents remove guard", () => {
  const scoped = findToolByName("package.scoped-registry")!;
  assert.equal(scoped.handler, "offline");
  assert.match(scoped.description, /force=true/, "scoped registry remove should explain force override");
  assert.match(scoped.description, /--dry-run/, "scoped registry remove should explain dry-run preview");
  assert.match(JSON.stringify(scoped.inputSchema), /force/, "scoped registry schema should expose force");
  assert.match(JSON.stringify(scoped.outputSchema), /matchedPackages/, "scoped registry output should report matched packages");
});

test("package C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  // package-manager.ts + cli-commands/package.ts 错误提示
  const pm = await readFile(new URL("../src/package-manager.js", import.meta.url), "utf8");
  const cliPkg = await readFile(new URL("../src/cli-commands/package.js", import.meta.url), "utf8");
  assert.equal(pm.includes("unity_cli tools run package"), false, "package-manager must not reference legacy form");
  assert.equal(cliPkg.includes("unity_cli tools run package"), false, "cli-commands/package must not reference legacy form");
  assert.match(pm, /tools run package\.list/, "package-manager hints should route to tools run package.list");
  assert.match(cliPkg, /tools run package\.list/, "cli package hints should route to tools run package.list");
});

test("package.embed bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/package.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "package"\)/, "bridge should forward structured package");
  assert.match(spec, /boolField\(flags, "dryRun"\)/, "bridge should forward structured dryRun");
});
