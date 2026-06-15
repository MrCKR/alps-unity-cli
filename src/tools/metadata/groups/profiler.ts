import type { ToolMetadata } from "../types.js";
import { objSchema, pageSchema } from "../schemas.js";

export const PROFILER_TOOLS: ToolMetadata[] = [
  {
    name: "profiler.status",
    title: "Profiler Status",
    description: "Check whether the Unity profiler is currently active.",
    group: "profiler",
    inputSchema: objSchema({}),
    readOnlyHint: true,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "profiler status",
    docsTopic: "profiler"
  },
  {
    name: "profiler.start",
    title: "Start Profiler",
    description: "Begin profiler data collection with optional deep profiling.",
    group: "profiler",
    inputSchema: objSchema({ deepProfiling: { type: "boolean" } }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "profiler start",
    docsTopic: "profiler"
  },
  {
    name: "profiler.stop",
    title: "Stop Profiler",
    description: "Stop profiler data collection.",
    group: "profiler",
    inputSchema: objSchema({}),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "bridge",
    cliCommand: "profiler stop",
    docsTopic: "profiler"
  },
  {
    name: "profiler.capture",
    title: "Capture Profiler Data",
    description: "Capture profiler frame data to a file. Can run as a long job.",
    group: "profiler",
    inputSchema: objSchema({ path: { type: "string" }, durationMs: { type: "integer" }, frames: { type: "integer" }, format: { type: "string", enum: ["json", "raw"] }, overwrite: { type: "boolean" } }, ["path"]),
    readOnlyHint: true,
    destructiveHint: false,
    longRunning: true,
    supportsDryRun: false,
    handler: "hybrid",
    cliCommand: "profiler capture",
    docsTopic: "profiler",
    examples: [
      { description: "Capture 1 second of profiler data", argv: ["profiler", "capture", "profile.json", "--duration-ms", "1000"] },
      { description: "Capture as job with wait", argv: ["profiler", "capture", "profile.json", "--job", "--wait"] }
    ]
  }
];
