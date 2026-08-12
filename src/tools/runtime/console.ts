import { defineTool } from "../define.js";

export type RuntimeConsoleLevel = "log" | "warning" | "error";
export interface RuntimeConsoleRequest { since?: number; limit?: number; level?: RuntimeConsoleLevel; includeStackTrace?: boolean }
export interface RuntimeConsoleEntry {
  sequence: number;
  observedAt: string;
  frameCount: number;
  level: RuntimeConsoleLevel;
  message: string;
  stackTrace: string;
}
export interface RuntimeConsoleResult {
  runtimeSessionId: string;
  cursor: number;
  droppedBeforeSequence: number;
  count: number;
  entries: RuntimeConsoleEntry[];
}

export default defineTool<RuntimeConsoleRequest, RuntimeConsoleResult>({
  name: "runtime.console",
  summary: "Read bounded console entries from the selected development Player.",
  description: "Read a bounded in-memory log page owned by the current development Player session. since is exclusive (entries require sequence > since), so use the returned cursor as since on the next call. level is a minimum severity: log includes info/log, warning includes warning/error, and error includes only error. Process exit invalidates the cursor and buffer.",
  group: "runtime",
  handler: "bridge-runtime",
  target: "runtime",
  destructiveHint: false,
  sideEffects: [],
  supportsDryRun: false,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Read up to 50 current Player console entries.", argv: ["tools", "run", "runtime.console", "--input", "{\"limit\":50}"], input: { limit: 50 } }],
  execute() { throw new Error("runtime.console must execute through the bridge-runtime dispatcher"); },
});
