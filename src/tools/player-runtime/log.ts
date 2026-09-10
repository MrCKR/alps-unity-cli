import { defineTool } from "../define.js";

export type RuntimeLogLevel = "info" | "warning" | "error";
export interface RuntimeLogRequest { message: string; level?: RuntimeLogLevel }
export interface RuntimeLogResult {
  runtimeSessionId: string;
  dryRun: boolean;
  logged: boolean;
  level: RuntimeLogLevel;
  message: string;
  sequence: number | null;
  observedAt: string;
  plannedEffects: Array<{ type: "runtime-log"; level: RuntimeLogLevel }>;
}

export default defineTool<RuntimeLogRequest, RuntimeLogResult>({
  name: "player-runtime.log",
  summary: "Write one bounded message to the selected development Player log.",
  description: "Validate and write one bounded message to the current development Player log, then return the exact in-memory console sequence as fresh session-bound proof. level=info is observed by player-runtime.console as level=log. Dry-run performs validation without writing.",
  group: "player-runtime",
  handler: "bridge-runtime",
  target: "runtime",
  destructiveHint: false,
  sideEffects: ["runtime-session"],
  supportsDryRun: true,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Write an informational Player log entry.", argv: ["tools", "run", "player-runtime.log", "--input", "{\"message\":\"runtime probe\",\"level\":\"info\"}"], input: { message: "runtime probe", level: "info" } }],
  execute() { throw new Error("player-runtime.log must execute through the bridge-runtime dispatcher"); },
});
