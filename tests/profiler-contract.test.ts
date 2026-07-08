import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
import { destructiveCommands } from "../src/security-policy.js";
import { makeLiveTool } from "./helpers/live-tool-fixtures.js";
import { contractToolNames, discoverLiveTools, listCsFiles, readEditorCsSources } from "./helpers/csharp-test-helpers.js";

const PROFILER_COMMANDS = [
  "profiler.areas",
  "profiler.capture",
  "profiler.clear",
  "profiler.counter",
  "profiler.deep",
  "profiler.export",
  "profiler.frame",
  "profiler.frame-debugger.disable",
  "profiler.frame-debugger.enable",
  "profiler.frame-debugger.events",
  "profiler.hierarchy",
  "profiler.load",
  "profiler.memory",
  "profiler.memory-snapshot.compare",
  "profiler.memory-snapshot.list",
  "profiler.memory-snapshot.take",
  "profiler.module-enable",
  "profiler.modules",
  "profiler.object-memory",
  "profiler.pause",
  "profiler.rendering-stats",
  "profiler.script-stats",
  "profiler.start",
  "profiler.status",
  "profiler.stop"
];

test("profiler group exposes all 25 tools via contract discovery", () => {
  assert.deepEqual(contractToolNames("profiler"), [...PROFILER_COMMANDS].sort());
});

test("profiler live descriptors expose representative play mode, package, and schema semantics", async () => {
  const tools = await discoverLiveTools(
    makeLiveTool({ name: "profiler.status", summary: "Read profiler status.", description: "Read profiler status.", group: "profiler", outputSchema: { type: "object", properties: { maxUsedMemory: { type: "number" }, platform: { type: "string" } } } }),
    makeLiveTool({ name: "profiler.counter", summary: "Read a profiler counter.", description: "Read a profiler counter.", group: "profiler" }),
    makeLiveTool({ name: "profiler.memory", summary: "Read profiler memory.", description: "Read profiler memory.", group: "profiler" }),
    makeLiveTool({ name: "profiler.frame", summary: "Read profiler frame data.", description: "Read profiler frame data.", group: "profiler", outputSchema: { type: "object", properties: { frame: { type: "object" } } } }),
    makeLiveTool({ name: "profiler.modules", summary: "List profiler modules.", description: "List profiler modules.", group: "profiler" }),
    makeLiveTool({ name: "profiler.areas", summary: "List profiler areas.", description: "List profiler areas.", group: "profiler" }),
    makeLiveTool({ name: "profiler.object-memory", summary: "Read object memory.", description: "Read object memory.", group: "profiler" }),
    makeLiveTool({ name: "profiler.rendering-stats", summary: "Read rendering stats.", description: "Read rendering stats.", group: "profiler" }),
    makeLiveTool({ name: "profiler.script-stats", summary: "Read script stats.", description: "Read script stats.", group: "profiler" }),
    makeLiveTool({ name: "profiler.deep", summary: "Read deep profiling stats.", description: "Read deep profiling stats.", group: "profiler" }),
    makeLiveTool({ name: "profiler.hierarchy", summary: "Read profiler hierarchy.", description: "Read profiler hierarchy with childrenTruncated summaries.", group: "profiler", inputSchema: { type: "object", properties: { top: { type: "integer", default: 20 }, depth: { type: "integer", default: 5 } } } }),
    makeLiveTool({ name: "profiler.memory-snapshot.list", summary: "List memory snapshots.", description: "List memory snapshots.", group: "profiler", requiresPackages: ["com.unity.memoryprofiler"] }),
    makeLiveTool({ name: "profiler.memory-snapshot.compare", summary: "Compare memory snapshots.", description: "Compare memory snapshots.", group: "profiler", requiresPackages: ["com.unity.memoryprofiler"] }),
    makeLiveTool({ name: "profiler.frame-debugger.events", summary: "Read frame debugger events.", description: "Read frame debugger events.", group: "profiler" }),
    makeLiveTool({ name: "profiler.start", summary: "Start the profiler.", description: "Start the profiler.", group: "profiler", playModeSupport: "session-affecting", inputSchema: { type: "object", properties: { logFile: { type: "string" }, enableCallstacks: { type: "boolean" } } } }),
    makeLiveTool({ name: "profiler.stop", summary: "Stop the profiler.", description: "Stop the profiler.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.pause", summary: "Pause the profiler.", description: "Pause the profiler.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.module-enable", summary: "Enable a profiler module.", description: "Enable a profiler module.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.clear", summary: "Clear profiler data.", description: "Clear profiler data.", group: "profiler", supportsDryRun: true, playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.load", summary: "Load profiler capture.", description: "Load profiler capture.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.capture", summary: "Capture profiler data.", description: "Capture profiler data.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.export", summary: "Export profiler data.", description: "Export profiler data.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.frame-debugger.enable", summary: "Enable the frame debugger.", description: "Enable the frame debugger.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.frame-debugger.disable", summary: "Disable the frame debugger.", description: "Disable the frame debugger.", group: "profiler", playModeSupport: "session-affecting" }),
    makeLiveTool({ name: "profiler.memory-snapshot.take", summary: "Take a memory snapshot.", description: "Take a memory snapshot.", group: "profiler", requiresPackages: ["com.unity.memoryprofiler"] })
  );

  for (const name of ["profiler.status", "profiler.counter", "profiler.memory", "profiler.frame", "profiler.modules", "profiler.areas", "profiler.object-memory", "profiler.rendering-stats", "profiler.script-stats", "profiler.deep", "profiler.hierarchy", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.events"]) {
    assert.equal(tools.get(name)?.playModeSupport, "supported", `${name} should be supported`);
  }

  for (const name of ["profiler.start", "profiler.stop", "profiler.pause", "profiler.module-enable", "profiler.clear", "profiler.load", "profiler.capture", "profiler.export", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable"]) {
    assert.equal(tools.get(name)?.playModeSupport, "session-affecting", `${name} should be session-affecting`);
  }

  const clear = tools.get("profiler.clear")!;
  assert.equal(clear.destructiveHint, false);
  assert.equal(clear.supportsDryRun, true);

  const hierarchy = tools.get("profiler.hierarchy")!;
  const props = hierarchy.inputSchema.properties as Record<string, { default?: unknown }>;
  assert.equal(props.top.default, 20);
  assert.equal(props.depth.default, 5);
  assert.match(hierarchy.description, /childrenTruncated/);

  const start = tools.get("profiler.start")!;
  const startProps = start.inputSchema.properties as Record<string, unknown>;
  assert.ok(startProps.logFile);
  assert.ok(startProps.enableCallstacks);

  for (const name of ["profiler.memory-snapshot.take", "profiler.memory-snapshot.list", "profiler.memory-snapshot.compare"]) {
    assert.deepEqual(tools.get(name)?.requiresPackages, ["com.unity.memoryprofiler"]);
  }

  const status = tools.get("profiler.status")!;
  assert.ok(status.outputSchema);
  const frame = tools.get("profiler.frame")!;
  assert.ok(frame.outputSchema);
  assert.equal(tools.get("profiler.memory-snapshot.take")?.destructiveHint, false);
  assert.equal(tools.get("profiler.frame-debugger.enable")?.destructiveHint, false);
  assert.equal(tools.get("profiler.frame-debugger.disable")?.destructiveHint, false);
});

test("profiler.hierarchy C# supports current hierarchy API and latest-frame default", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/hierarchy.cs", import.meta.url), "utf8");
  assert.match(source, /UnityEditor\.Profiling\.HierarchyFrameDataView[\s\S]*UnityEditorInternal\.HierarchyFrameDataView[\s\S]*UnityEngine\.Profiling\.HierarchyFrameDataView/);
  assert.match(source, /typeof\(bool\)/);
  assert.match(source, /ProfilerDriverIntProperty\("lastFrameIndex", -1\)/);
});

test("profiler.start records Editor frames and profiler.stop restores profileEditor", async () => {
  const controlSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/control-tools.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  const source = `${controlSource}\n${supportSource}`;
  assert.match(source, /GetProperty\("profileEditor"/);
  assert.match(source, /PreviousProfilerEditorRecording = ProfilerToolSupport\.GetProfilerEditorRecording\(\)/);
  assert.match(source, /SetProfilerEditorRecording\(true\)[\s\S]*SetProfilerSessionEnabled\(true\)/);
  assert.match(source, /SetProfilerEditorRecording\(ProfilerToolSupport\.PreviousProfilerEditorRecording\.Value\)/);
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
    assert.equal(contract?.csharp_handler, "present", `${name} should have present C# handler`);
  }
});

test("C# RegisterCommand calls stay removed for migrated profiler tools", async () => {
  const allSource = await readEditorCsSources();
  const migrated = [...PROFILER_COMMANDS];
  for (const command of migrated) {
    assert.ok(!allSource.includes(`RegisterCommand(registry, "${command}"`), `${command} should NOT be registered (migrated to IUnityCliTool/IUnityCliDeferredTool)`);
  }
});

test("profiler.clear is not in the destructive security policy", () => {
  const commands = new Set(destructiveCommands().map((entry) => entry.command));
  assert.equal(commands.has("profiler.clear"), false);
});

test("profiler C# has no legacy unity_cli command form", async () => {
  const files = await listCsFiles(new URL("../unitypkg/Editor/Tools/profiler/", import.meta.url));
  const source = (await Promise.all(files.map((file) => readFile(file, "utf8")))).join("\n");
  assert.equal(source.includes("unity_cli "), false, "must not reference legacy 'unity_cli ' command form");
});

test("profiler.object-memory/script-stats migrated to IUnityCliTool", async () => {
  const toolsSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/read-tools.cs", import.meta.url), "utf8");
  const actionSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/action-tools.cs", import.meta.url), "utf8");
  assert.match(toolsSource, /class ProfilerScriptStatsTool/);
  assert.match(actionSource, /class ProfilerObjectMemoryTool/);
});

test("profiler bridge spec forwards structured --input fields", async () => {
  const spec = await readFile(new URL("../src/bridge/domains/profiler.js", import.meta.url), "utf8");
  assert.match(spec, /stringField\(flags, "module"\)/);
  assert.match(spec, /numberField\(flags, "durationMs"\)/);
  assert.match(spec, /boolField\(flags, "deepProfiling"\)/);
  assert.match(spec, /stringField\(flags, "logFile"\)/);
  assert.match(spec, /boolField\(flags, "enableCallstacks"\)/);
  assert.match(spec, /stringField\(flags, "target"\)/);
});

test("profiler.start accepts logFile and enableCallstacks", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/control-tools.cs", import.meta.url), "utf8");
  assert.match(source, /public string LogFile \{ get; set; \}/);
  assert.match(source, /public bool EnableCallstacks \{ get; set; \}/);
  assert.match(source, /Profiler\.enableAllocationCallstacks = true/);
});

test("profiler.status exposes maxUsedMemory and platform", async () => {
  const statusSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/status.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  const source = `${statusSource}\n${supportSource}`;
  assert.match(source, /MaxUsedMemoryBytes = \(long\)Profiler\.maxUsedMemory/);
  assert.match(source, /Profiler\.supported/);
  assert.match(source, /Application\.platform\.ToString\(\)/);
  assert.match(source, /IsFrameTimingAvailable\(\)/);
});

test("profiler.frame includes FrameTimingManager fields", async () => {
  const readSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/read-tools.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  const source = `${readSource}\n${supportSource}`;
  assert.match(source, /BuildProfilerFrame\(request\.Module \?\? "CPU Usage"\)/);
  assert.match(source, /FrameTimingManager\.CaptureFrameTimings\(\)/);
  assert.match(source, /CpuMainThreadFrameTimeMs/);
  assert.match(source, /GpuFrameTimeMs/);
  assert.match(source, /SyncInterval/);
});

test("profiler.module-enable calls SetAreaEnabled", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/action-tools.cs", import.meta.url), "utf8");
  assert.match(source, /GetMethod\("SetAreaEnabled"/);
  assert.match(source, /FindProfilerAreaType\(\)/);
  assert.match(source, /Enum\.Parse\(areaType, request\.Module, true\)/);
  assert.doesNotMatch(source, /Editor prefs/);
});

test("profiler.load validates capture path before reporting success", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/action-tools.cs", import.meta.url), "utf8");
  assert.match(source, /File\.Exists\(resolvedPath\)/);
  assert.match(source, /E_PROFILER_CAPTURE_NOT_FOUND/);
  assert.match(source, /Loaded = true/);
});

test("profiler memory-snapshot and frame-debugger tools migrated to IUnityCliTool", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable", "profiler.frame-debugger.events"]) {
    assert.ok(!allSource.includes(`RegisterCommand(registry, "${command}"`));
    assert.ok(allSource.includes(`Name = "${command}"`));
  }
  const memorySource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot-tools.cs", import.meta.url), "utf8");
  assert.match(memorySource, /class ProfilerMemorySnapshotListTool : IUnityCliTool</);
  assert.match(memorySource, /class ProfilerMemorySnapshotCompareTool : IUnityCliTool</);
  const frameSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/frame-debugger-tools.cs", import.meta.url), "utf8");
  assert.match(frameSource, /class ProfilerFrameDebuggerEnableTool : IUnityCliTool</);
  assert.match(frameSource, /class ProfilerFrameDebuggerDisableTool : IUnityCliTool</);
  assert.match(frameSource, /class ProfilerFrameDebuggerEventsTool : IUnityCliTool</);
  assert.match(frameSource, /FrameDebuggerUtility/);
});

test("profiler.capture and memory-snapshot.take migrated to IUnityCliDeferredTool", async () => {
  const captureSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/capture.cs", import.meta.url), "utf8");
  assert.match(captureSource, /class ProfilerCaptureTool : IUnityCliDeferredTool</);
  assert.match(captureSource, /bool TryDefer\(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion\)/);
  const snapshotSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot-take.cs", import.meta.url), "utf8");
  assert.match(snapshotSource, /class ProfilerMemorySnapshotTakeTool : IUnityCliDeferredTool</);
  assert.match(snapshotSource, /bool TryDefer\(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion\)/);
});
