import { defineTool } from "../../define.js";

export type RuntimePointerEvent = "move" | "down" | "up" | "click";
export type RuntimePointerButton = "left" | "right" | "middle";
export interface RuntimeInputPointerRequest { x: number; y: number; event?: RuntimePointerEvent; button?: RuntimePointerButton }
export interface RuntimePoint { x: number; y: number }
export interface RuntimeInputPointerResult {
  runtimeSessionId: string;
  dryRun: boolean;
  simulated: boolean;
  event: RuntimePointerEvent;
  button: RuntimePointerButton;
  controlPath: string;
  beforePosition: RuntimePoint;
  afterPosition: RuntimePoint;
  beforePressed: boolean;
  afterPressed: boolean;
  observedPressedStates: boolean[];
  plannedEffects: Array<{ type: "runtime-input-pointer"; x: number; y: number; event: RuntimePointerEvent; button: RuntimePointerButton }>;
}

export default defineTool<RuntimeInputPointerRequest, RuntimeInputPointerResult>({
  name: "runtime.input.pointer",
  summary: "Inject one bounded Input System pointer event into a development Player.",
  description: "Resolve the current Input System mouse, validate in-screen pixel coordinates, queue move, down, up, or click only in the selected development Player, flush the event, and return fresh position and pressed-state observations.",
  group: "runtime",
  handler: "bridge-runtime",
  target: "runtime",
  requiresPackages: ["com.unity.inputsystem"],
  destructiveHint: false,
  sideEffects: ["runtime-session"],
  supportsDryRun: true,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Click the left pointer button at screen position 100,100.", argv: ["tools", "run", "runtime.input.pointer", "--input", "{\"x\":100,\"y\":100,\"event\":\"click\",\"button\":\"left\"}"], input: { x: 100, y: 100, event: "click", button: "left" } }],
  execute() { throw new Error("runtime.input.pointer must execute through the bridge-runtime dispatcher"); },
});
