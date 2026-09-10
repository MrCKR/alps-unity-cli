import { defineTool } from "../../define.js";

export interface RuntimeTimeScaleSetRequest { scale: number }
export interface RuntimeTimeScaleSetResult {
  runtimeSessionId: string;
  dryRun: boolean;
  changed: boolean;
  before: number;
  after: number;
  plannedEffects: Array<{ type: "runtime-time-scale-set"; scale: number }>;
}

export default defineTool<RuntimeTimeScaleSetRequest, RuntimeTimeScaleSetResult>({
  name: "player-runtime.time-scale.set",
  summary: "Set and read back the current development Player time scale.",
  description: "Validate one finite non-negative session-only scale, apply it to the selected development Player, and return fresh Time.timeScale readback. Use 0 to pause and 1 for normal time.",
  group: "player-runtime",
  handler: "bridge-runtime",
  target: "runtime",
  destructiveHint: false,
  sideEffects: ["runtime-session"],
  supportsDryRun: true,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Run the current Player at half time scale.", argv: ["tools", "run", "player-runtime.time-scale.set", "--input", "{\"scale\":0.5}"], input: { scale: 0.5 } }],
  execute() { throw new Error("player-runtime.time-scale.set must execute through the bridge-runtime dispatcher"); },
});
