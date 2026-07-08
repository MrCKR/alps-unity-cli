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

const PROFILER_COMMANDS = ["profiler.status", "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.modules", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats", "profiler.deep", "profiler.hierarchy", "profiler.memory-snapshot.take", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable", "profiler.frame-debugger.events"];

/**
 * Profiler 域契约测试（23 工具）。
 * 对齐 IvanMurzak modules/clear/load/script-stats 与 CoplayDev areas/object-memory/rendering-stats，
 * 新增 memory-snapshot（take/list/compare）与 frame-debugger（enable/disable/events），
 * 验证 metadata/bridge/contract/C# 一致，read 工具业务字段不被 envelope 吞掉，Play Mode 语义。
 */

test("profiler group exposes all 25 tools", () => {
  const tools = filterToolsByGroup("profiler").map((t) => t.name).sort();
  assert.equal(tools.length, 25);
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
  for (const name of ["profiler.status", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.modules", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats", "profiler.deep", "profiler.hierarchy", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.events"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "supported", `${name} should be supported`);
  }
});

test("profiler control tools are session-affecting", () => {
  for (const name of ["profiler.start", "profiler.stop", "profiler.pause", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.capture", "profiler.export", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable"]) {
    const tool = findToolByName(name)!;
    assert.equal(tool.playModeSupport, "session-affecting", `${name} should be session-affecting`);
  }
});

test("profiler.clear is non-destructive with dry-run", () => {
  const clear = findToolByName("profiler.clear")!;
  assert.equal(clear.destructiveHint, false);
  assert.equal(clear.supportsDryRun, true);
});

test("profiler.hierarchy exposes bounded defaults and truncation summaries", () => {
  const hierarchy = findToolByName("profiler.hierarchy")!;
  const props = hierarchy.inputSchema.properties as Record<string, { default?: unknown }>;
  assert.equal(props.top.default, 20);
  assert.equal(props.depth.default, 5);
  assert.match(hierarchy.description, /childrenTruncated/, "profiler.hierarchy should document truncated children");
});

test("profiler.hierarchy C# supports current hierarchy API and latest-frame default", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/hierarchy.cs", import.meta.url), "utf8");
  assert.match(source, /UnityEditor\.Profiling\.HierarchyFrameDataView[\s\S]*UnityEditorInternal\.HierarchyFrameDataView[\s\S]*UnityEngine\.Profiling\.HierarchyFrameDataView/, "profiler.hierarchy should try UnityEditor.Profiling first, then compatibility type names");
  assert.match(source, /typeof\(bool\)/, "Unity 2022.3 GetHierarchyFrameDataView sortAscending parameter should be bool");
  assert.match(source, /ProfilerDriverIntProperty\("lastFrameIndex", -1\)/, "omitted frame should resolve to ProfilerDriver.lastFrameIndex");
});

test("profiler.start records Editor frames and profiler.stop restores profileEditor", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.match(source, /GetProperty\("profileEditor"/, "profiler should reflect ProfilerDriver.profileEditor");
  assert.match(source, /previousProfilerEditorRecording = GetProfilerEditorRecording\(\)/, "profiler.start should remember previous profileEditor state");
  assert.match(source, /SetProfilerEditorRecording\(true\)[\s\S]*SetProfilerSessionEnabled\(true\)/, "profiler.start should enable Profile Editor before recording");
  assert.match(source, /SetProfilerEditorRecording\(previousProfilerEditorRecording\.Value\)/, "profiler.stop should restore previous profileEditor state");
});

test("bridge specs resolve for profiler tools", () => {
  for (const [cmd, expected] of [["status", "project-tool.run"], ["start", "project-tool.run"], ["stop", "project-tool.run"], ["pause", "project-tool.run"], ["counter", "project-tool.run"], ["memory", "project-tool.run"], ["frame", "project-tool.run"], ["capture", "project-tool.run"], ["export", "project-tool.run"], ["modules", "project-tool.run"], ["module-enable", "project-tool.run"], ["clear", "project-tool.run"], ["load", "project-tool.run"], ["areas", "project-tool.run"], ["object-memory", "project-tool.run"], ["rendering-stats", "project-tool.run"], ["script-stats", "project-tool.run"], ["deep", "project-tool.run"], ["hierarchy", "project-tool.run"], ["memory-snapshot.take", "project-tool.run"], ["memory-snapshot.list", "project-tool.run"], ["memory-snapshot.compare", "project-tool.run"], ["frame-debugger.enable", "project-tool.run"], ["frame-debugger.disable", "project-tool.run"], ["frame-debugger.events", "project-tool.run"]] as const) {
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

test("C# RegisterCommand calls exist for legacy profiler commands (deferred/complex-reflection)", async () => {
  const csFiles = await listCsFiles(new URL("../unitypkg/Editor/", import.meta.url));
  const allSource = (
    await Promise.all(
      csFiles.filter((f) => !f.pathname.includes("/Samples/")).map((f) => readFile(f, "utf8"))
    )
  ).join("\n");
  // 迁移后的 profiler 工具不再用 RegisterCommand(含 capture/memory-snapshot.take 迁为 IUnityCliDeferredTool)
  const migrated = ["profiler.status", "profiler.start", "profiler.stop", "profiler.pause", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.capture", "profiler.export", "profiler.modules", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.areas", "profiler.object-memory", "profiler.script-stats", "profiler.deep", "profiler.hierarchy", "profiler.rendering-stats", "profiler.memory-snapshot.take", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable", "profiler.frame-debugger.events"];
  for (const command of migrated) {
    assert.ok(!allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should NOT be registered (migrated to IUnityCliTool/IUnityCliDeferredTool)`);
  }
});

test("profiler.clear is not in the destructive security policy", () => {
  const commands = new Set(destructiveCommands().map((e) => e.command));
  assert.equal(commands.has("profiler.clear"), false);
});

test("profiler C# has no legacy unity_cli command form", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.equal(source.includes("unity_cli "), false, "must not reference legacy 'unity_cli ' command form");
});

test("profiler.object-memory/script-stats migrated to IUnityCliTool", async () => {
  const toolsSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/read-tools.cs", import.meta.url), "utf8");
  const actionSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/action-tools.cs", import.meta.url), "utf8");
  assert.match(toolsSource, /class ProfilerScriptStatsTool/, "ProfilerScriptStatsTool should exist");
  assert.match(actionSource, /class ProfilerObjectMemoryTool/, "ProfilerObjectMemoryTool should exist");
});

test("profiler bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/profiler.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "module"\)/, "bridge should forward structured module");
  assert.match(spec, /numberField\(flags, "durationMs"\)/, "bridge should forward structured durationMs");
  assert.match(spec, /boolField\(flags, "deepProfiling"\)/, "bridge should forward structured deepProfiling");
  assert.match(spec, /stringField\(flags, "logFile"\)/, "bridge should forward structured logFile");
  assert.match(spec, /boolField\(flags, "enableCallstacks"\)/, "bridge should forward structured enableCallstacks");
  assert.match(spec, /stringField\(flags, "target"\)/, "bridge should forward structured target");
});

test("profiler.start accepts logFile and enableCallstacks", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.match(source, /StringArg\("logFile"\)/, "ProfilerStart should read logFile");
  assert.match(source, /BoolArg\("enableCallstacks"/, "ProfilerStart should read enableCallstacks");
  assert.match(source, /Profiler\.enableAllocationCallstacks = true/, "ProfilerStart should enable allocation callstacks");
  const start = findToolByName("profiler.start")!;
  const props = start.inputSchema.properties as Record<string, unknown>;
  assert.ok(props.logFile, "profiler.start should expose logFile");
  assert.ok(props.enableCallstacks, "profiler.start should expose enableCallstacks");
});

test("profiler.status exposes maxUsedMemory and platform", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.match(source, /maxUsedMemoryBytes/, "ProfilerStateJson should include maxUsedMemoryBytes");
  assert.match(source, /Profiler\.supported/, "ProfilerStateJson should use real Profiler.supported");
  assert.match(source, /Application\.platform\.ToString/, "ProfilerStatus should include platform");
  assert.match(source, /IsFrameTimingAvailable/, "ProfilerStatus should include frameTimingAvailable");
  const status = findToolByName("profiler.status")!;
  assert.ok(status.outputSchema, "profiler.status should have outputSchema");
});

test("profiler.frame includes FrameTimingManager fields", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.match(source, /ProfilerFrameTimingJson/, "ProfilerFrameJson should call ProfilerFrameTimingJson");
  assert.match(source, /FrameTimingManager\.CaptureFrameTimings/, "should capture frame timings");
  assert.match(source, /cpuMainThreadFrameTimeMs/, "should expose cpuMainThreadFrameTimeMs");
  assert.match(source, /gpuFrameTimeMs/, "should expose gpuFrameTimeMs");
  assert.match(source, /syncInterval/, "should expose syncInterval");
  const frame = findToolByName("profiler.frame")!;
  assert.ok(frame.outputSchema, "profiler.frame should have outputSchema");
});

test("profiler.module-enable calls SetAreaEnabled", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.match(source, /Profiler\.SetAreaEnabled/, "ProfilerModuleEnable should reference SetAreaEnabled");
  assert.match(source, /UnityEngine\.Profiling\.ProfilerArea/, "ProfilerModuleEnable should resolve the Unity 2022+ ProfilerArea enum");
  assert.match(source, /Enum\.Parse\(areaType/, "ProfilerModuleEnable should parse the area enum via reflection");
  assert.doesNotMatch(source, /Module enable\/disable uses Editor prefs/, "ProfilerModuleEnable must not return the old note-only stub");
});

test("profiler.load validates capture path before reporting success", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/action-tools.cs", import.meta.url), "utf8");
  assert.match(source, /File\.Exists\(resolvedPath\)/, "profiler.load should check the capture file exists");
  assert.match(source, /E_PROFILER_CAPTURE_NOT_FOUND/, "missing capture path should return a stable error code");
  assert.match(source, /Loaded = true/, "successful load should expose loaded=true");
});

test("profiler memory-snapshot and frame-debugger tools migrated to IUnityCliTool", async () => {
  // memory-snapshot.take 迁移为 IUnityCliDeferredTool(不再用 RegisterCommand + StartDeferredWait)
  const commandsSource = await readFile(new URL("../unitypkg/Editor/Commands/ProfilerCommands.cs", import.meta.url), "utf8");
  assert.ok(!commandsSource.includes(`RegisterCommand(registry, "profiler.memory-snapshot.take"`), "profiler.memory-snapshot.take should NOT be registered (migrated to IUnityCliDeferredTool)");
  // list/compare/frame-debugger.* 迁移为 IUnityCliTool
  const memorySource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot-tools.cs", import.meta.url), "utf8");
  assert.match(memorySource, /class ProfilerMemorySnapshotListTool/, "ProfilerMemorySnapshotListTool should exist");
  assert.match(memorySource, /class ProfilerMemorySnapshotCompareTool/, "ProfilerMemorySnapshotCompareTool should exist");
  const frameSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/frame-debugger-tools.cs", import.meta.url), "utf8");
  assert.match(frameSource, /class ProfilerFrameDebuggerEnableTool/, "ProfilerFrameDebuggerEnableTool should exist");
  assert.match(frameSource, /class ProfilerFrameDebuggerDisableTool/, "ProfilerFrameDebuggerDisableTool should exist");
  assert.match(frameSource, /class ProfilerFrameDebuggerEventsTool/, "ProfilerFrameDebuggerEventsTool should exist");
  // frame-debugger 反射 FrameDebuggerUtility
  assert.match(frameSource, /FrameDebuggerUtility/, "frame-debugger should reflect FrameDebuggerUtility");
});

test("profiler.capture and memory-snapshot.take migrated to IUnityCliDeferredTool", async () => {
  const captureSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/capture.cs", import.meta.url), "utf8");
  assert.match(captureSource, /class ProfilerCaptureTool : IUnityCliDeferredTool</, "ProfilerCaptureTool should implement IUnityCliDeferredTool");
  assert.match(captureSource, /bool TryDefer\(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion\)/, "ProfilerCaptureTool should declare TryDefer");
  const snapshotSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot-take.cs", import.meta.url), "utf8");
  assert.match(snapshotSource, /class ProfilerMemorySnapshotTakeTool : IUnityCliDeferredTool</, "ProfilerMemorySnapshotTakeTool should implement IUnityCliDeferredTool");
  assert.match(snapshotSource, /bool TryDefer\(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion\)/, "ProfilerMemorySnapshotTakeTool should declare TryDefer");
});

test("profiler memory-snapshot tools declare requiresPackages", () => {
  for (const name of ["profiler.memory-snapshot.take", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare"]) {
    const tool = findToolByName(name)!;
  assert.deepEqual(tool.requiresPackages, ["com.unity.memoryprofiler"], `${name} should require com.unity.memoryprofiler`);
  }
});

test("profiler memory-snapshot.take and frame-debugger.enable are non-destructive", () => {
  // 生成 .snap 产物文件与 Frame Debugger 会话切换均非资产文件持久化，不触发 --yes
  assert.equal(findToolByName("profiler.memory-snapshot.take")!.destructiveHint, false);
  assert.equal(findToolByName("profiler.frame-debugger.enable")!.destructiveHint, false);
  assert.equal(findToolByName("profiler.frame-debugger.disable")!.destructiveHint, false);
});
