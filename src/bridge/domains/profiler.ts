import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const profilerBridgeSpecs: BridgeCommandSpec[] = [
  { group: "profiler", command: "start", bridgeCommand: "catalog.editor.run", toolName: "profiler.start", buildArgs: profilerArgs },
  { group: "profiler", command: "stop", bridgeCommand: "catalog.editor.run", toolName: "profiler.stop", buildArgs: profilerArgs },
  { group: "profiler", command: "pause", bridgeCommand: "catalog.editor.run", toolName: "profiler.pause", buildArgs: profilerArgs },
  { group: "profiler", command: "counter", bridgeCommand: "catalog.editor.run", toolName: "profiler.counter", buildArgs: profilerArgs },
  { group: "profiler", command: "memory", bridgeCommand: "catalog.editor.run", toolName: "profiler.memory", buildArgs: profilerArgs },
  { group: "profiler", command: "frame", bridgeCommand: "catalog.editor.run", toolName: "profiler.frame", buildArgs: profilerArgs },
  { group: "profiler", command: "capture", bridgeCommand: "catalog.editor.run", toolName: "profiler.capture", buildArgs: profilerArgs, destructiveHint: true },
  { group: "profiler", command: "export", bridgeCommand: "catalog.editor.run", toolName: "profiler.export", buildArgs: profilerArgs, destructiveHint: true },
  { group: "profiler", command: "status", bridgeCommand: "catalog.editor.run", toolName: "profiler.status", buildArgs: profilerArgs },
  { group: "profiler", command: "modules", bridgeCommand: "catalog.editor.run", toolName: "profiler.modules", buildArgs: profilerArgs },
  { group: "profiler", command: "module-enable", bridgeCommand: "catalog.editor.run", toolName: "profiler.module-enable", buildArgs: profilerModuleEnableArgs },
  { group: "profiler", command: "clear", bridgeCommand: "catalog.editor.run", toolName: "profiler.clear", buildArgs: profilerArgs, supportsDryRun: true },
  { group: "profiler", command: "load", bridgeCommand: "catalog.editor.run", toolName: "profiler.load", buildArgs: profilerArgs },
  { group: "profiler", command: "areas", bridgeCommand: "catalog.editor.run", toolName: "profiler.areas", buildArgs: profilerArgs },
  { group: "profiler", command: "object-memory", bridgeCommand: "catalog.editor.run", toolName: "profiler.object-memory", buildArgs: profilerObjectMemoryArgs },
  { group: "profiler", command: "deep", bridgeCommand: "catalog.editor.run", toolName: "profiler.deep", buildArgs: ({ flags }) => compactArgs({ enabled: boolFlag(flags, "enabled") ?? boolField(flags, "enabled") }) },
  { group: "profiler", command: "hierarchy", bridgeCommand: "catalog.editor.run", toolName: "profiler.hierarchy", buildArgs: profilerHierarchyArgs },
  { group: "profiler", command: "rendering-stats", bridgeCommand: "catalog.editor.run", toolName: "profiler.rendering-stats", buildArgs: profilerArgs },
  { group: "profiler", command: "script-stats", bridgeCommand: "catalog.editor.run", toolName: "profiler.script-stats", buildArgs: profilerArgs },
  { group: "profiler", command: "memory-snapshot-take", bridgeCommand: "catalog.editor.run", toolName: "profiler.memory-snapshot-take", buildArgs: profilerMemorySnapshotArgs },
  { group: "profiler", command: "memory-snapshot-list", bridgeCommand: "catalog.editor.run", toolName: "profiler.memory-snapshot-list", buildArgs: profilerMemorySnapshotArgs },
  { group: "profiler", command: "memory-snapshot-compare", bridgeCommand: "catalog.editor.run", toolName: "profiler.memory-snapshot-compare", buildArgs: profilerMemorySnapshotCompareArgs },
  { group: "profiler", command: "frame-debugger-enable", bridgeCommand: "catalog.editor.run", toolName: "profiler.frame-debugger-enable", buildArgs: ({ flags }) => compactArgs({}) },
  { group: "profiler", command: "frame-debugger-disable", bridgeCommand: "catalog.editor.run", toolName: "profiler.frame-debugger-disable", buildArgs: ({ flags }) => compactArgs({}) },
  { group: "profiler", command: "frame-debugger-events", bridgeCommand: "catalog.editor.run", toolName: "profiler.frame-debugger-events", buildArgs: profilerFrameDebuggerEventsArgs }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段
function structuredInput(flags: BridgeFlags): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function stringField(flags: BridgeFlags, key: string): string | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "string" ? value : undefined;
}

function numberField(flags: BridgeFlags, key: string): number | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "number" ? value : undefined;
}

function boolField(flags: BridgeFlags, key: string): boolean | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "boolean" ? value : undefined;
}

function profilerArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    ...queryArgs(flags),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "output") ?? stringField(flags, "path"),
    module: stringFlag(flags, "module") ?? stringField(flags, "module"),
    category: stringFlag(flags, "category") ?? stringField(flags, "category"),
    counter: stringFlag(flags, "counter") ?? stringField(flags, "counter"),
    format: stringFlag(flags, "format") ?? stringField(flags, "format"),
    durationMs: numberFlag(flags, "duration-ms") ?? numberField(flags, "durationMs"),
    frames: numberFlag(flags, "frames") ?? numberField(flags, "frames"),
    overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite"),
    deepProfiling: boolFlag(flags, "deep-profiling") ?? boolField(flags, "deepProfiling"),
    logFile: stringFlag(flags, "log-file") ?? stringField(flags, "logFile"),
    enableCallstacks: boolFlag(flags, "enable-callstacks") ?? boolField(flags, "enableCallstacks"),
    paused: boolFlag(flags, "paused") ?? boolField(flags, "paused")
  });
}

function profilerModuleEnableArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ module: stringFlag(flags, "module") ?? stringField(flags, "module"), enabled: boolFlag(flags, "enabled") ?? boolField(flags, "enabled") });
}

function profilerObjectMemoryArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"), path: stringFlag(flags, "path") ?? stringField(flags, "path") });
}

// memory-snapshot.take/list：path 可选
function profilerMemorySnapshotArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"),
    maxBytes: numberFlag(flags, "max-bytes") ?? numberField(flags, "maxBytes"),
    pageSize: numberFlag(flags, "page-size") ?? numberField(flags, "pageSize"),
    cursor: stringFlag(flags, "cursor") ?? stringField(flags, "cursor"),
    budget: structured.budget
  });
}

// memory-snapshot.compare：两个快照路径（snapshotA/B 或 pathA/B 别名）
function profilerMemorySnapshotCompareArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    snapshotA: stringFlag(flags, "snapshot-a") ?? stringFlag(flags, "path-a") ?? stringField(flags, "snapshotA") ?? stringField(flags, "pathA"),
    snapshotB: stringFlag(flags, "snapshot-b") ?? stringFlag(flags, "path-b") ?? stringField(flags, "snapshotB") ?? stringField(flags, "pathB"),
    budget: structuredInput(flags).budget
  });
}

// frame-debugger.events：分页
function profilerFrameDebuggerEventsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    pageSize: numberFlag(flags, "page-size") ?? numberField(flags, "pageSize"),
    cursor: stringFlag(flags, "cursor") ?? stringField(flags, "cursor"),
    budget: structuredInput(flags).budget
  });
}

// hierarchy：指定帧/线程、全局页大小、深度和 raw continuation。
function profilerHierarchyArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    frame: numberFlag(flags, "frame") ?? numberField(flags, "frame"),
    thread: numberFlag(flags, "thread") ?? numberField(flags, "thread"),
    pageSize: numberFlag(flags, "page-size") ?? numberField(flags, "pageSize"),
    depth: numberFlag(flags, "depth") ?? numberField(flags, "depth"),
    cursor: stringFlag(flags, "cursor") ?? stringField(flags, "cursor"),
    budget: structuredInput(flags).budget
  });
}
