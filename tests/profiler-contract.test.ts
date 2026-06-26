import assert from "node:assert/strict";
import test from "node:test";
import { findToolByName, filterToolsByGroup } from "../src/tools/metadata/index.js";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { destructiveCommands } from "../src/security-policy.js";
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

const PROFILER_COMMANDS = ["profiler.status", "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.modules", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats"];

/**
 * Profiler 域契约测试（17 工具）。
 * 对齐 IvanMurzak modules/clear/load/script-stats 与 CoplayDev areas/object-memory/rendering-stats，
 * 验证 metadata/bridge/contract/C# 一致，3 个 read 工具业务字段不被 envelope 吞掉，Play Mode 语义。
 */

test("profiler group exposes all 17 tools", () => {
  const tools = filterToolsByGroup("profiler").map((t) => t.name).sort();
  assert.equal(tools.length, 17);
  for (const name of PROFILER_COMMANDS) {
    assert.ok(tools.includes(name), `${name} should exist`);
  }
});

test("profiler tools have playModeSupport", () => {
  for (const name of PROFILER_COMMANDS) {
    const tool = findToolByName(name)!;
    assert.ok(tool.playModeSupport, `${name} should have playModeSupport`);
  }
});

test("profiler read tools are supported", () => {
  for (const name of ["profiler.status", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.modules", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported", `${name} should be supported`);
  }
});

test("profiler control tools are session-affecting", () => {
  for (const name of ["profiler.start", "profiler.stop", "profiler.pause", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.capture", "profiler.export"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} should be session-affecting`);
  }
});

test("profiler.clear is destructive with dry-run", () => {
  const clear = findToolByName("profiler.clear")!;
  assert.equal(clear.destructiveHint, true);
  assert.equal(clear.supportsDryRun, true);
});

test("bridge specs resolve for profiler tools", () => {
  for (const [cmd, expected] of [["status", "profiler.status"], ["start", "profiler.start"], ["stop", "profiler.stop"], ["pause", "profiler.pause"], ["counter", "profiler.counter"], ["memory", "profiler.memory"], ["frame", "profiler.frame"], ["capture", "profiler.capture"], ["export", "profiler.export"], ["modules", "profiler.modules"], ["module-enable", "profiler.module-enable"], ["clear", "profiler.clear"], ["load", "profiler.load"], ["areas", "profiler.areas"], ["object-memory", "profiler.object-memory"], ["rendering-stats", "profiler.rendering-stats"], ["script-stats", "profiler.script-stats"]] as const) {
    const spec = bridgeCommandRegistry.lookup("profiler", cmd);
    assert.equal(spec?.bridgeCommand, expected, `profiler ${cmd} should resolve`);
  }
});

test("contract entries exist for profiler tools with present C# handler", () => {
  for (const name of PROFILER_COMMANDS) {
    const contract = findCommandContract(name);
    assert.ok(contract, `${name} should be in contract`);
    assert.equal(contract.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls exist for profiler commands", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  for (const command of PROFILER_COMMANDS) {
    assert.ok(allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should be registered`);
  }
});

test("destructive security policy covers profiler.clear", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.ok(commands.has("profiler.clear"));
});

test("profiler C# has no legacy unity_cli command form", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli "), false, "must not reference legacy 'unity_cli ' command form");
});

test("profiler.object-memory/rendering-stats/script-stats C# wrap in SuccessJson", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  // object-memory
  const omBlock = source.slice(source.indexOf("ProfilerObjectMemory(BridgeRequest request)"), source.indexOf("private static string ProfilerRenderingStats"));
  assert.match(omBlock, /return SuccessJson\(w\.EndObject/, "ProfilerObjectMemory must wrap in SuccessJson");
  // rendering-stats
  const rsBlock = source.slice(source.indexOf("ProfilerRenderingStats(BridgeRequest request)"), source.indexOf("private static string ProfilerScriptStats"));
  assert.match(rsBlock, /return SuccessJson\(w\.EndObject/, "ProfilerRenderingStats must wrap in SuccessJson");
  // script-stats
  const ssBlock = source.slice(source.indexOf("ProfilerScriptStats(BridgeRequest request)"), source.indexOf("private static string ProfilerCounter("));
  assert.match(ssBlock, /return SuccessJson\(w\.EndObject/, "ProfilerScriptStats must wrap in SuccessJson");
});

test("profiler bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/profiler.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "module"\)/, "bridge should forward structured module");
  assert.match(spec, /numberField\(flags, "durationMs"\)/, "bridge should forward structured durationMs");
  assert.match(spec, /boolField\(flags, "deepProfiling"\)/, "bridge should forward structured deepProfiling");
  assert.match(spec, /stringField\(flags, "target"\)/, "bridge should forward structured target");
});
