import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { readFile, readdir } from "node:fs/promises";

/** 递归列举 .cs 文件。 */
async function listCsFiles(directory: URL): Promise<URL[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const urls = await Promise.all(entries.map(async (entry) => {
    const child = new URL(`${entry.name}${entry.isDirectory() ? "/" : ""}`, directory);
    if (entry.isDirectory()) {
      return listCsFiles(child);
    }
    return entry.isFile() && entry.name.endsWith(".cs") ? [child] : [];
  }));
  return urls.flat().sort((left, right) => left.pathname.localeCompare(right.pathname));
}

const SCRIPT_COMMANDS = ["script.create", "script.hash", "script.edit", "script.apply-edits", "script.validate", "script.update-or-create", "script.capabilities"];

/**
 * Script 域契约测试（create/hash/edit/apply-edits/validate/update-or-create/capabilities）。
 * 对齐 CoplayDev manage_script / script_apply_edits 的结构化编辑与 SHA precondition，
 * 验证 metadata/bridge/contract/C# 一致，错误恢复提示迁移到 `tools run` 形态，
 * script.capabilities 业务字段不被 envelope 吞掉。
 */

test("script write tools exist with correct safety hints and play mode", () => {
  const create = findToolByName("script.create")!;
  assert.equal(create.playModeSupport, "session-affecting");
  assert.ok(create.sideEffects?.includes("session-affecting"));
  assert.equal(create.riskLevel, "medium");

  const edit = findToolByName("script.edit")!;
  assert.equal(edit.destructiveHint, true);
  assert.equal(edit.supportsDryRun, true);
  assert.equal(edit.riskLevel, "high");
  assert.equal(edit.playModeSupport, "session-affecting");

  const applyEdits = findToolByName("script.apply-edits")!;
  assert.equal(applyEdits.destructiveHint, true);
  assert.equal(applyEdits.riskLevel, "high");
  assert.match(applyEdits.description, /balanced/, "apply-edits should advertise brace balancing");

  const updateOrCreate = findToolByName("script.update-or-create")!;
  assert.equal(updateOrCreate.destructiveHint, true);
  assert.equal(updateOrCreate.supportsDryRun, true);
  assert.equal(updateOrCreate.riskLevel, "high");
});

test("script read-only tools are supported in any play mode", () => {
  for (const name of ["script.hash", "script.validate", "script.capabilities"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.destructiveHint, false, `${name} non-destructive`);
    assert.equal(tool.playModeSupport, "supported", `${name} play mode supported`);
    assert.ok(tool.examples?.length || name === "script.capabilities", `${name} examples (capabilities may omit)`);
  }
});

test("script tools expose output schemas", () => {
  const hash = findToolByName("script.hash")!;
  assert.ok((hash.outputSchema?.properties as Record<string, unknown>)?.sha256, "hash output should expose sha256");
  const validate = findToolByName("script.validate")!;
  assert.ok((validate.outputSchema?.properties as Record<string, unknown>)?.valid, "validate output should expose valid");
  const caps = findToolByName("script.capabilities")!;
  assert.ok((caps.outputSchema?.properties as Record<string, unknown>)?.capabilities, "capabilities output should expose capabilities");
});

test("bridge specs resolve for script tools", () => {
  for (const [cmd, expected] of [["create", "script.create"], ["hash", "script.hash"], ["edit", "script.edit"], ["apply-edits", "script.apply-edits"], ["validate", "script.validate"], ["update-or-create", "script.update-or-create"], ["capabilities", "script.capabilities"]] as const) {
    const spec = bridgeCommandRegistry.lookup("script", cmd);
    assert.equal(spec?.bridgeCommand, expected, `script ${cmd} should resolve`);
  }
});

test("contract entries exist for script tools with present C# handler", () => {
  for (const name of SCRIPT_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for script commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of SCRIPT_COMMANDS) {
    assert.ok(allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should be registered`);
  }
});

test("script C# error recovery hints use current tools run form, not legacy unity_cli", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ScriptCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli script read"), false, "must not reference legacy 'unity_cli script read'");
  assert.equal(source.includes("unity_cli asset search"), false, "must not reference legacy 'unity_cli asset search'");
  assert.match(source, /tools run script\.read/, "error hints should route to tools run script.read");
  assert.match(source, /tools run asset\.search/, "error hints should route to tools run asset.search");
});

test("script.capabilities C# wraps result in SuccessJson so business fields survive the envelope", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ScriptCommands.cs", import.meta.url), "utf8");
  const block = source.slice(source.indexOf("ScriptCapabilities(BridgeRequest request)"), source.indexOf("private static string ScriptValidate"));
  assert.match(block, /return SuccessJson\(w\.EndObject/, "ScriptCapabilities must wrap payload in SuccessJson");
});
