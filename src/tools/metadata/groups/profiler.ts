import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const PROFILER_TOOLS: ToolMetadata[] = [
  {
    name: "profiler.status",
    summary: "Check whether the Unity profiler is currently active, with max-used memory and platform support.",
    description: "Read the current Unity Profiler state, available modules, max-used memory and platform support. Use this before start/stop/pause when you need to know whether recording is already active.",
    group: "profiler",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ profiler: { type: "object" }, modules: { type: "array", items: { type: "string" } }, platform: { type: "string" }, frameTimingAvailable: { type: "boolean" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.start",
    summary: "Begin profiler data collection with optional deep profiling, binary log file and allocation callstacks.",
    description: "Begin Unity Profiler data collection in the Editor session. This enables profiling state and may enable deep profiling, binary logging or allocation callstacks; use profiler.stop when the sample window is complete.",
    group: "profiler",
    inputSchema: objSchema({ deepProfiling: { type: "boolean" }, logFile: { type: "string", description: "Binary profiler log file path; sets Profiler.logFile + enableBinaryLog." }, enableCallstacks: { type: "boolean", description: "Enable allocation callstacks for memory analysis." } }),
    outputSchema: objSchema({ profiler: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Start profiler collection", argv: ["tools", "run", "profiler.start", "--input", "{\"deepProfiling\":false}"] },
      { description: "Start with allocation callstacks", argv: ["tools", "run", "profiler.start", "--input", "{\"enableCallstacks\":true}"] }
    ],
  },
  {
    name: "profiler.stop",
    summary: "Stop profiler data collection.",
    description: "Stop Unity Profiler data collection and restore the previous Editor profiling state when one was saved by profiler.start. Returns the latest profiler state and plannedEffects so callers can verify recording stopped before reading captured frames.",
    group: "profiler",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ profiler: { type: "object" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Stop profiler collection", argv: ["tools", "run", "profiler.stop", "--input", "{}"] }
    ],
  },

  {
    name: "profiler.pause",
    summary: "Pause or resume Profiler data collection by toggling Unity Profiler.enabled.",
    description: "Pause or resume Profiler data collection by toggling Unity Profiler.enabled for the current Editor session. Pass paused=true to pause recording or paused=false to resume; omit paused to pause the current enabled state.",
    group: "profiler",
    inputSchema: objSchema({ paused: { type: "boolean" } }),
    outputSchema: objSchema({ profiler: { type: "object" }, paused: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Pause profiler recording", argv: ["tools", "run", "profiler.pause", "--input", "{\"paused\":true}"] },
      { description: "Resume profiler recording", argv: ["tools", "run", "profiler.pause", "--input", "{\"paused\":false}"] }
    ],
  },
  {
    name: "profiler.counter",
    summary: "Read current profiler counter values and available counter metadata.",
    description: "Read current profiler counter values and available counter metadata, optionally narrowed by category and counter name. This is read-only and does not start or stop recording.",
    group: "profiler",
    inputSchema: objSchema({ category: { type: "string" }, counter: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.memory",
    summary: "Query Unity memory usage from the live Editor.",
    description: "Query Unity memory usage from the live Editor, including values exposed by UnityEngine.Profiling.Profiler. Use object-memory when you need per-object/component runtime memory size.",
    group: "profiler",
    inputSchema: objSchema({}),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.frame",
    summary: "Read current frame count, timing, selected profiler module diagnostics and FrameTimingManager CPU/GPU frame timing fields.",
    description: "Read current frame count, timing, selected profiler module diagnostics and FrameTimingManager CPU/GPU timing fields. Use module when you want diagnostics for one Profiler area.",
    group: "profiler",
    inputSchema: objSchema({ module: { type: "string" } }),
    outputSchema: objSchema({ profiler: { type: "object" }, frame: { type: "object", description: "Includes frameCount, deltaTime and frameTiming with 12 FrameTimingManager fields (cpuFrameTimeMs, gpuFrameTimeMs, etc.)." }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.capture",
    summary: "Capture profiler frame data to a file with optional tag and frame-range metadata.",
    description: "Capture profiler frame data to a file. Supports timed (durationMs) or fixed-frame (frames) captures and can run as a long job. The response includes startFrame/endFrame so the raw capture can be aligned to logical sampling points. Pass captureTag to also write a <path>.metadata.json sidecar (requestId, captureTag, frame range, timestamps) for archival and correlation with screenshots or external logs.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string" }, durationMs: { type: "integer" }, frames: { type: "integer" }, format: { type: "string", enum: ["json", "raw"] }, overwrite: { type: "boolean" }, captureTag: { type: "string", description: "Optional logical sampling tag. When set, a <path>.metadata.json sidecar is written alongside the artifact for archival/correlation." } }, ["path"]),
    destructiveHint: false,
    supportsDryRun: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Capture 1 second of profiler data", argv: ["tools", "run", "profiler.capture", "profile.json", "--duration-ms", "1000"] },
      { description: "Capture as batchmode job with wait", argv: ["tools", "run", "profiler.capture", "--input", "{\"path\":\"profile.json\",\"format\":\"json\",\"overwrite\":true}", "--job", "--wait", "--project", "<closed-project-path>", "--unity", "<Unity.exe>"] },
      { description: "Capture raw with a correlation tag and frame range sidecar", argv: ["tools", "run", "profiler.capture", "--input", "{\"path\":\"baseline.raw\",\"format\":\"raw\",\"frames\":30,\"captureTag\":\"baseline-before-opt\"}"] }
    ],
  },
  {
    name: "profiler.export",
    summary: "Export a profiler snapshot artifact in JSON or raw profiler format.",
    description: "Export a profiler snapshot artifact in JSON or raw profiler format to the requested path. This writes an artifact file and may overwrite only when overwrite=true.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string" }, format: { type: "string", enum: ["json", "raw"] }, overwrite: { type: "boolean" } }, ["path"]),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Export profiler JSON", argv: ["tools", "run", "profiler.export", "profile.json", "--overwrite"] }
    ],
  },
  {
    name: "profiler.modules",
    summary: "List available Profiler modules and their visibility state.",
    description: "List available Profiler modules and their visibility state. Use the returned module names with profiler.module-enable when toggling collection areas.",
    group: "profiler",
    inputSchema: objSchema({}),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.module-enable",
    summary: "Enable or disable a Profiler module (area) by name via Profiler.SetAreaEnabled.",
    description: "Enable or disable a Profiler module (area) by name via Profiler.SetAreaEnabled. Run profiler.areas to list valid area names; the change affects the current Editor profiling session.",
    group: "profiler",
    inputSchema: objSchema({ module: { type: "string" }, enabled: { type: "boolean", default: true } }, ["module"]),
    outputSchema: objSchema({ command: { type: "string" }, module: { type: "string" }, enabled: { type: "boolean" }, wasEnabled: { type: "boolean" }, isEnabled: { type: "boolean" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Enable a Profiler area", argv: ["tools", "run", "profiler.module-enable", "--input", "{\"module\":\"CPU\",\"enabled\":true}"] },
      { description: "Disable a Profiler area", argv: ["tools", "run", "profiler.module-enable", "--input", "{\"module\":\"Rendering\",\"enabled\":false}"] }
    ],
  },
  {
    name: "profiler.clear",
    summary: "Clear all collected profiler data.",
    description: "Clear all collected profiler data from the current Editor session. This discards profiling history; use --dry-run first when you need to confirm the planned effect without clearing samples.",
    group: "profiler",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, dryRun: { type: "boolean" }, cleared: { type: "boolean" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    supportsDryRun: true,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Preview clearing profiler data", argv: ["tools", "run", "profiler.clear", "--input", "{}", "--dry-run"] },
      { description: "Clear profiler data", argv: ["tools", "run", "profiler.clear", "--input", "{}"] }
    ],
  },
  {
    name: "profiler.load",
    summary: "Load a saved profiler data file (.raw/.data) into the Profiler window.",
    description: "Load a saved profiler data file (.raw/.data) into the Profiler window for the current Editor session. This changes Profiler window state but does not modify the source capture file.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string" } }, ["path"]),
    outputSchema: objSchema({ command: { type: "string" }, path: { type: "string" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Load a profiler data file", argv: ["tools", "run", "profiler.load", "--input", "{\"path\":\"Artifacts/profiler/baseline.raw\"}"] }
    ],
  },
  {
    name: "profiler.areas",
    summary: "List all Profiler area enum names.",
    description: "List all Profiler area enum names accepted by profiler.module-enable. This is read-only and returns the enum type, count and area names for the current Unity version.",
    group: "profiler",
    inputSchema: objSchema({}),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.object-memory",
    summary: "Query memory size of components on a GameObject via Profiler.GetRuntimeMemorySizeLong.",
    description: "Query runtime memory size of components on a GameObject via Profiler.GetRuntimeMemorySizeLong. target resolves by name/path/id; path can narrow to a specific child GameObject.",
    group: "profiler",
    inputSchema: objSchema({ target: { type: "string" }, path: { type: "string" } }, ["target"]),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.rendering-stats",
    summary: "Read UnityStats rendering counters (batches, triangles, vertices, setPassCalls) for the current frame, with an available flag when stats are null (non-rendering frame).",
    description: "Read rendering sample statistics from UnityEditor.UnityStats (batches, triangles, vertices, setPassCalls, screenRes, usedTextureMemory, renderTextureCount). These fields are only populated while the Game View is actively rendering; the response includes an `available` flag and diagnostics explaining when values are null, so callers do not mistake empty samples for data loss.",
    group: "profiler",
    inputSchema: objSchema({}),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.script-stats",
    summary: "Script/GC memory sample statistics (gcAllocs, monoHeap, monoUsed).",
    description: "Read Script/GC memory sample statistics such as gcAllocs, monoHeap and monoUsed from Unity Profiler counters. This is read-only and reflects the currently available Profiler sample data.",
    group: "profiler",
    inputSchema: objSchema({}),
    destructiveHint: false,
    playModeSupport: "supported",
  },
  {
    name: "profiler.deep",
    summary: "Read or toggle Unity Deep Profiling at runtime (must be stopped to change).",
    description: "Read or toggle Unity Deep Profiling (per-function call profiling). Pass enabled=true/false to change; omit enabled to read the current state. Unity only allows Deep Profiling to change while the Profiler is not recording, so changing it during an active session returns E_PROFILER_DEEP_REQUIRES_STOP with nextActions pointing at profiler.stop. Typical flow: profiler.stop, profiler.deep --enabled true, profiler.start.",
    group: "profiler",
    inputSchema: objSchema({ enabled: { type: "boolean", description: "Omit to read the current Deep Profiling state; set true/false to toggle. Toggling requires the Profiler to be stopped." } }),
    outputSchema: objSchema({ command: { type: "string" }, deepProfiling: { type: "boolean" }, previous: { type: "boolean" }, changed: { type: "boolean" }, profiler: { type: "object" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Read current Deep Profiling state", argv: ["tools", "run", "profiler.deep"] },
      { description: "Enable Deep Profiling before a capture", argv: ["tools", "run", "profiler.deep", "--input", "{\"enabled\":true}"] }
    ]
  },
  {
    name: "profiler.hierarchy",
    summary: "Read the per-function CPU call hierarchy (marker tree) for one profiler frame/thread.",
    description: "Read the CPU call hierarchy for a single profiler frame and thread via ProfilerDriver.GetHierarchyFrameDataView, recursing GetItemChildren to emit a structured marker tree. Defaults are bounded (top=20, depth=5); each node reports childCount and childrenTruncated so AI can see when detail was omitted.",
    group: "profiler",
    inputSchema: objSchema({ frame: { type: "integer", description: "Frame index to read; omit or -1 for the latest recorded frame." }, thread: { type: "integer", description: "Thread index, default 0 (main thread)." }, top: { type: "integer", default: 20, description: "Max children emitted per level (breadth)." }, depth: { type: "integer", default: 5, description: "Max recursion depth." } }),
    outputSchema: objSchema({ command: { type: "string" }, frame: { type: "integer" }, thread: { type: "integer" }, rootItems: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    examples: [
      { description: "Latest frame main-thread top markers", argv: ["tools", "run", "profiler.hierarchy", "--input", "{\"top\":20,\"depth\":5}"] },
      { description: "Specific frame/thread", argv: ["tools", "run", "profiler.hierarchy", "--input", "{\"frame\":100,\"thread\":0}"] }
    ]
  },
  {
    name: "profiler.memory-snapshot.take",
    summary: "Capture a memory snapshot (.snap) via MemoryProfiler.",
    description: "Capture a memory snapshot (.snap) via MemoryProfiler. Requires package com.unity.memoryprofiler. Capture is asynchronous and may take several seconds.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string", description: "Output .snap path. Defaults to <tempCachePath>/MemoryCaptures/snapshot_<timestamp>.snap." } }),
    outputSchema: objSchema({ command: { type: "string" }, path: { type: "string" }, sizeBytes: { type: "integer" }, plannedEffects: { type: "array", items: { type: "object" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    requiresPackages: ["com.unity.memoryprofiler"],
    examples: [
      { description: "Take a memory snapshot to a custom path", argv: ["tools", "run", "profiler.memory-snapshot.take", "--input", "{\"path\":\"Artifacts/snap1.snap\"}"] }
    ]
  },
  {
    name: "profiler.memory-snapshot.list",
    summary: "List .snap memory snapshot files in the default capture directories or a custom path.",
    description: "List .snap memory snapshot files in the default capture directories or a custom path. This reads file metadata only and does not open or modify snapshots.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string", description: "Directory to search. Defaults to MemoryCaptures under tempCachePath and project root." } }),
    outputSchema: objSchema({ command: { type: "string" }, snapshots: { type: "array", items: { type: "object" } }, searchedDirs: { type: "array", items: { type: "string" } }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    requiresPackages: ["com.unity.memoryprofiler"],
  },
  {
    name: "profiler.memory-snapshot.compare",
    summary: "Compare two .snap memory snapshots at file-level metadata (size delta, time delta).",
    description: "Compare two .snap memory snapshots at file-level metadata (size delta, time delta). For object-level diff, open both in the Memory Profiler window.",
    group: "profiler",
    inputSchema: objSchema({ snapshotA: { type: "string" }, snapshotB: { type: "string" }, pathA: { type: "string", description: "Alias for snapshotA." }, pathB: { type: "string", description: "Alias for snapshotB." } }, ["snapshotA", "snapshotB"]),
    outputSchema: objSchema({ command: { type: "string" }, snapshotA: { type: "object" }, snapshotB: { type: "object" }, delta: { type: "object" }, note: { type: "string" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
    requiresPackages: ["com.unity.memoryprofiler"],
  },
  {
    name: "profiler.frame-debugger.enable",
    summary: "Enable the Unity Frame Debugger to capture draw call events.",
    description: "Enable the Unity Frame Debugger to capture draw call events for the current Editor session. In Play Mode the game must be paused first with editor.pause; this opens the Frame Debugger window and returns the captured event count.",
    group: "profiler",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, enabled: { type: "boolean" }, eventCount: { type: "integer" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Enable Frame Debugger", argv: ["tools", "run", "profiler.frame-debugger.enable", "--input", "{}"] }
    ],
  },
  {
    name: "profiler.frame-debugger.disable",
    summary: "Disable the Unity Frame Debugger.",
    description: "Disable the Unity Frame Debugger for the current Editor session. Use this after profiler.frame-debugger.events when draw-call inspection is complete; it returns enabled=false without modifying project assets.",
    group: "profiler",
    inputSchema: objSchema({}),
    outputSchema: objSchema({ command: { type: "string" }, enabled: { type: "boolean" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "session-affecting",
    examples: [
      { description: "Disable Frame Debugger", argv: ["tools", "run", "profiler.frame-debugger.disable", "--input", "{}"] }
    ],
  },
  {
    name: "profiler.frame-debugger.events",
    summary: "Read draw call events captured by the Frame Debugger, paginated.",
    description: "Read draw call events captured by the Frame Debugger, paginated. Returns shaderName, vertexCount, indexCount and similar fields per event when available.",
    group: "profiler",
    inputSchema: objSchema({ pageSize: { type: "integer", default: 50 }, cursor: { type: "integer", default: 0 } }),
    outputSchema: objSchema({ command: { type: "string" }, totalEvents: { type: "integer" }, pageSize: { type: "integer" }, cursor: { type: "integer" }, events: { type: "array", items: { type: "object" } }, nextCursor: { type: "integer" }, logPath: { type: "string" } }),
    destructiveHint: false,
    playModeSupport: "supported",
  }
];
