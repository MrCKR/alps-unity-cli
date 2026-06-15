import { boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const profilerBridgeSpecs: BridgeCommandSpec[] = [
  { group: "profiler", command: "start", bridgeCommand: "profiler.start", buildArgs: profilerArgs },
  { group: "profiler", command: "stop", bridgeCommand: "profiler.stop", buildArgs: profilerArgs },
  { group: "profiler", command: "pause", bridgeCommand: "profiler.pause", buildArgs: profilerArgs },
  { group: "profiler", command: "counter", bridgeCommand: "profiler.counter", buildArgs: profilerArgs },
  { group: "profiler", command: "memory", bridgeCommand: "profiler.memory", buildArgs: profilerArgs },
  { group: "profiler", command: "frame", bridgeCommand: "profiler.frame", buildArgs: profilerArgs },
  { group: "profiler", command: "capture", bridgeCommand: "profiler.capture", buildArgs: profilerArgs, supportsJob: true },
  { group: "profiler", command: "export", bridgeCommand: "profiler.export", buildArgs: profilerArgs },
  { group: "profiler", command: "status", bridgeCommand: "profiler.status", buildArgs: profilerArgs }
];

function profilerArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...queryArgs(flags),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "output"),
    module: stringFlag(flags, "module"),
    category: stringFlag(flags, "category"),
    counter: stringFlag(flags, "counter"),
    format: stringFlag(flags, "format"),
    durationMs: numberFlag(flags, "duration-ms"),
    frames: numberFlag(flags, "frames"),
    overwrite: boolFlag(flags, "overwrite"),
    deepProfiling: boolFlag(flags, "deep-profiling"),
    paused: boolFlag(flags, "paused")
  });
}
