import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const profilerBridgeSpecs: BridgeCommandSpec[] = [
  { group: "profiler", command: "start", bridgeCommand: "profiler.start", buildArgs: profilerArgs },
  { group: "profiler", command: "stop", bridgeCommand: "profiler.stop", buildArgs: profilerArgs },
  { group: "profiler", command: "pause", bridgeCommand: "profiler.pause", buildArgs: profilerArgs },
  { group: "profiler", command: "counter", bridgeCommand: "profiler.counter", buildArgs: profilerArgs },
  { group: "profiler", command: "memory", bridgeCommand: "profiler.memory", buildArgs: profilerArgs },
  { group: "profiler", command: "frame", bridgeCommand: "profiler.frame", buildArgs: profilerArgs },
  { group: "profiler", command: "capture", bridgeCommand: "profiler.capture", buildArgs: profilerArgs, supportsJob: true },
  { group: "profiler", command: "export", bridgeCommand: "profiler.export", buildArgs: profilerArgs },
  { group: "profiler", command: "status", bridgeCommand: "profiler.status", buildArgs: profilerArgs },
  { group: "profiler", command: "modules", bridgeCommand: "profiler.modules", buildArgs: profilerArgs },
  { group: "profiler", command: "module-enable", bridgeCommand: "profiler.module-enable", buildArgs: profilerModuleEnableArgs },
  { group: "profiler", command: "clear", bridgeCommand: "profiler.clear", buildArgs: profilerArgs },
  { group: "profiler", command: "load", bridgeCommand: "profiler.load", buildArgs: profilerArgs },
  { group: "profiler", command: "areas", bridgeCommand: "profiler.areas", buildArgs: profilerArgs },
  { group: "profiler", command: "object-memory", bridgeCommand: "profiler.object-memory", buildArgs: profilerObjectMemoryArgs },
  { group: "profiler", command: "rendering-stats", bridgeCommand: "profiler.rendering-stats", buildArgs: profilerArgs },
  { group: "profiler", command: "script-stats", bridgeCommand: "profiler.script-stats", buildArgs: profilerArgs }
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
    paused: boolFlag(flags, "paused") ?? boolField(flags, "paused")
  });
}

function profilerModuleEnableArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ module: stringFlag(flags, "module") ?? stringField(flags, "module"), enabled: boolFlag(flags, "enabled") ?? boolField(flags, "enabled") });
}

function profilerObjectMemoryArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"), path: stringFlag(flags, "path") ?? stringField(flags, "path") });
}
