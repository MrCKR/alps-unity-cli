import { defineTool } from "../../define.js";

export type RuntimeKeyEvent = "down" | "up" | "press";
export interface RuntimeInputKeyRequest { key: string; event?: RuntimeKeyEvent }
export interface RuntimeInputKeyResult {
  runtimeSessionId: string;
  dryRun: boolean;
  simulated: boolean;
  key: string;
  event: RuntimeKeyEvent;
  controlPath: string;
  beforePressed: boolean;
  afterPressed: boolean;
  observedPressedStates: boolean[];
  plannedEffects: Array<{ type: "runtime-input-key"; key: string; event: RuntimeKeyEvent }>;
}

export default defineTool<RuntimeInputKeyRequest, RuntimeInputKeyResult>({
  name: "player-runtime.input.key",
  summary: "Inject one bounded Input System key event into a development Player.",
  description: "Resolve one current Input System keyboard control, queue down, up, or press only in the selected development Player, flush the event, and return fresh pressed-state observations. The Tool is unavailable when com.unity.inputsystem is absent.",
  group: "player-runtime",
  handler: "bridge-runtime",
  target: "runtime",
  requiresPackages: ["com.unity.inputsystem"],
  destructiveHint: false,
  sideEffects: ["runtime-session"],
  supportsDryRun: true,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Press Space in the current development Player.", argv: ["tools", "run", "player-runtime.input.key", "--input", "{\"key\":\"Space\",\"event\":\"press\"}"], input: { key: "Space", event: "press" } }],
  execute() { throw new Error("player-runtime.input.key must execute through the bridge-runtime dispatcher"); },
});
