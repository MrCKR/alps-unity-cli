import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { findCommandContract } from "../src/contracts/command-contract.js";
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
  assert.match(source, /GetMethod\("GetRawFrameDataView"[\s\S]*RawHierarchyAccessor/);
  assert.match(source, /GetSampleChildrenCountRecursive/);
  assert.doesNotMatch(source, /GetSampleChildAt/);
  assert.match(source, /UnityCliContinuationToken/);
  assert.match(source, /request\.Frame \?\? ProfilerToolSupport\.ProfilerDriverIntProperty\("lastFrameIndex", -1\)/);
  assert.match(source, /Description = "Read latest-frame nodes"[\s\S]*PageSize = 50, Depth = 5/);
});

test("profiler.start records Editor frames and profiler.stop restores profileEditor", async () => {
  const startSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/start.cs", import.meta.url), "utf8");
  const stopSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/stop.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  const source = `${startSource}\n${stopSource}\n${supportSource}`;
  assert.match(source, /GetProperty\("profileEditor"/);
  assert.match(source, /PreviousProfilerEditorRecording = ProfilerToolSupport\.GetProfilerEditorRecording\(\)/);
  assert.match(source, /SetProfilerEditorRecording\(true\)[\s\S]*SetProfilerSessionEnabled\(true\)/);
  assert.match(source, /SetProfilerEditorRecording\(ProfilerToolSupport\.PreviousProfilerEditorRecording\.Value\)/);
  assert.doesNotMatch(supportSource, /HandlerFingerprint|handlerFingerprint/, "Profiler business DTO must not expose internal fingerprints");
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

test("profiler commands are exposed by typed C# tools", async () => {
  const allSource = await readEditorCsSources();
  for (const command of PROFILER_COMMANDS) {
    assert.ok(allSource.includes(`Name = "${command}"`), `${command} should expose its public tool name`);
  }
});

test("profiler.clear has no destructive metadata", () => {
  const command = bridgeCommandRegistry.lookup("profiler", "clear");
  assert.equal(command?.destructiveHint, undefined);
});

test("profiler.clear uses the Unity 2022.3 frame-clear API with exact readback", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/clear.cs", import.meta.url), "utf8");
  assert.match(source, /GetMethod\("ClearAllFrames"/);
  assert.doesNotMatch(source, /GetMethod\("Clear"/);
  assert.match(source, /firstFrameIndex >= 0 \|\| lastFrameIndex >= 0/);
  assert.match(source, /E_PROFILER_CLEAR_STATE_MISMATCH/);
});

test("profiler.object-memory and script-stats implement IUnityCliTool", async () => {
  const scriptStatsSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/script-stats.cs", import.meta.url), "utf8");
  const objectMemorySource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/object-memory.cs", import.meta.url), "utf8");
  assert.match(scriptStatsSource, /class ProfilerScriptStatsTool : IUnityCliTool/);
  assert.match(objectMemorySource, /class ProfilerObjectMemoryTool : IUnityCliTool/);
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
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/start.cs", import.meta.url), "utf8");
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
  const frameSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/frame.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  const source = `${frameSource}\n${supportSource}`;
  assert.match(source, /BuildProfilerFrame\(request\.Module \?\? "CPU Usage"\)/);
  assert.match(source, /FrameTimingManager\.CaptureFrameTimings\(\)/);
  assert.match(source, /CpuMainThreadFrameTimeMs/);
  assert.match(source, /GpuFrameTimeMs/);
  assert.match(source, /SyncInterval/);
});

test("profiler.module-enable calls SetAreaEnabled", async () => {
  const source = await readFile(new URL("../unitypkg/Editor/Tools/profiler/module-enable.cs", import.meta.url), "utf8");
  assert.match(source, /GetMethod\("SetAreaEnabled"/);
  assert.match(source, /FindProfilerAreaType\(\)/);
  assert.match(source, /Enum\.Parse\(areaType, request\.Module, true\)/);
  assert.doesNotMatch(source, /Editor prefs/);
});

test("profiler.load and capture require real terminal artifacts before reporting success", async () => {
  const loadSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/load.cs", import.meta.url), "utf8");
  const captureSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/capture.cs", import.meta.url), "utf8");
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  assert.match(loadSource, /File\.Exists\(resolvedPath\)/);
  assert.match(loadSource, /E_PROFILER_CAPTURE_NOT_FOUND/);
  assert.match(loadSource, /E_PROFILER_LOAD_UNSUPPORTED/);
  assert.match(loadSource, /E_PROFILER_LOAD_FAILED/);
  assert.match(loadSource, /GetMethod\("ClearAllFrames"/);
  assert.doesNotMatch(loadSource, /GetMethod\("Clear"/);
  assert.doesNotMatch(loadSource, /loadMethod\?\.Invoke/);
  assert.match(loadSource, /Loaded = true/);
  assert.match(captureSource, /File\.Delete\(outputPath\)/);
  assert.match(captureSource, /EnsureRawProfilerArtifact\(outputPath\)/);
  assert.match(supportSource, /E_PROFILER_RAW_ARTIFACT_MISSING/);
  assert.match(supportSource, /new FileInfo\(outputPath\)\.Length <= 0/);
  assert.match(supportSource, /E_PROFILER_METADATA_WRITE_FAILED/);
  assert.match(supportSource, /new FileInfo\(sidecarPath\)\.Length <= 0/);
  assert.doesNotMatch(supportSource, /sidecar 写入失败不阻塞主流程/);
  assert.doesNotMatch(supportSource, /Unity did not emit binary data/);
});

test("profiler memory-snapshot and frame-debugger tools implement IUnityCliTool", async () => {
  const allSource = await readEditorCsSources();
  for (const command of ["profiler.memory-snapshot.list", "profiler.memory-snapshot.compare", "profiler.frame-debugger.enable", "profiler.frame-debugger.disable", "profiler.frame-debugger.events"]) {
    assert.ok(!allSource.includes(`RegisterCommand(registry, "${command}"`));
    assert.ok(allSource.includes(`Name = "${command}"`));
  }
  const memoryListSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot/list.cs", import.meta.url), "utf8");
  const memoryCompareSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot/compare.cs", import.meta.url), "utf8");
  assert.match(memoryListSource, /class ProfilerMemorySnapshotListTool : IUnityCliTool</);
  assert.match(memoryCompareSource, /class ProfilerMemorySnapshotCompareTool : IUnityCliTool</);
  const frameEnableSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/frame-debugger/enable.cs", import.meta.url), "utf8");
  const frameDisableSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/frame-debugger/disable.cs", import.meta.url), "utf8");
  const frameEventsSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/frame-debugger/events.cs", import.meta.url), "utf8");
  assert.match(frameEnableSource, /class ProfilerFrameDebuggerEnableTool : IUnityCliTool/);
  assert.match(frameDisableSource, /class ProfilerFrameDebuggerDisableTool : IUnityCliTool/);
  assert.match(frameEventsSource, /class ProfilerFrameDebuggerEventsTool : IUnityCliTool/);
  assert.match(frameEventsSource, /public string Cursor \{ get; set; \}/);
  assert.match(frameEventsSource, /public string NextCursor \{ get; set; \}/);
  assert.match(frameEventsSource, /FrameDebuggerReflection\.CaptureGeneration/);
  assert.match(frameEventsSource, /UnityCliContinuationToken\.TryParse/);
  assert.match(frameEventsSource, /E_CONTINUATION_STALE/);
  assert.doesNotMatch(frameEventsSource, /non-resumable|intentionally provides no continuation/i);
  assert.match(`${frameEnableSource}\n${frameDisableSource}\n${frameEventsSource}`, /FrameDebuggerUtility/);
  const supportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerToolSupport.cs", import.meta.url), "utf8");
  const frameSupportSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerFrameDebuggerSupport.cs", import.meta.url), "utf8");
  assert.match(supportSource, /Type\.GetType\(fullName, false\)/);
  assert.match(supportSource, /fullName\.IndexOf\(','\)/);
  assert.match(frameSupportSource, /UnityEditorInternal\.FrameDebuggerInternal\.FrameDebuggerUtility, UnityEditor/);
  assert.match(frameSupportSource, /UnityEngine\.FrameDebugger, UnityEngine\.CoreModule/);
  assert.match(frameSupportSource, /GetProperty\("enabled", BindingFlags\.Public \| BindingFlags\.Static\)/);
  assert.doesNotMatch(frameSupportSource, /GetMethod\("IsLocalEnabled"/);
  assert.match(frameSupportSource, /new object\[\] \{ value, 0 \}/);
  assert.match(frameSupportSource, /UnityCliReadGeneration\.Session\("profiler\.frame-debugger\.events"\)/);
  assert.match(frameSupportSource, /Interlocked\.Read\(ref captureGeneration\)/);
  assert.doesNotMatch(frameSupportSource, /eventsCount|isEnabled|GetMethod\("GetEnabled"/);
});

test("profiler.capture and memory-snapshot.take use async IUnityCliTool", async () => {
  const captureSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/capture.cs", import.meta.url), "utf8");
  assert.match(captureSource, /class ProfilerCaptureTool : IUnityCliTool</);
  assert.match(captureSource, /public async ValueTask<Result> ExecuteAsync\(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default\)/);
  assert.match(captureSource, /await ctx\.Wait\.NextFrame\(1, ct\)/);
  assert.match(captureSource, /DestructiveHint = true/);
  assert.match(captureSource, /SideEffects = new\[\] \{ "persistent-artifact-write" \}/);
  assert.match(captureSource, /Multi-frame raw artifacts can be large, so start with frames=1 and increase deliberately\./);
  assert.match(captureSource, /Description = "Capture one raw profiler snapshot with tag"/);
  assert.match(captureSource, /Frames = 1/);
  assert.doesNotMatch(captureSource, /Frames = 30/);
  const snapshotSource = await readFile(new URL("../unitypkg/Editor/Tools/profiler/memory-snapshot/take.cs", import.meta.url), "utf8");
  const snapshotSupport = await readFile(new URL("../unitypkg/Editor/Tools/profiler/ProfilerMemorySnapshotSupport.cs", import.meta.url), "utf8");
  assert.match(snapshotSource, /class ProfilerMemorySnapshotTakeTool : IUnityCliTool</);
  assert.match(snapshotSource, /public async ValueTask<Result> ExecuteAsync\(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default\)/);
  assert.match(snapshotSource, /await ctx\.Wait\.ForCallback<Result>\(/);
  assert.match(snapshotSupport, /E_PROFILER_SNAPSHOT_ARTIFACT_MISSING/);
  assert.match(snapshotSupport, /if \(!exists\)[\s\S]*if \(length <= 0L\)/);
});
