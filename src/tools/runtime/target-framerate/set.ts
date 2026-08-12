import { defineTool } from "../../define.js";

export interface RuntimeTargetFramerateSetRequest { frameRate: number }
export interface RuntimeTargetFramerateSetResult {
  runtimeSessionId: string;
  dryRun: boolean;
  changed: boolean;
  before: number;
  after: number;
  plannedEffects: Array<{ type: "runtime-target-framerate-set"; frameRate: number }>;
}

export default defineTool<RuntimeTargetFramerateSetRequest, RuntimeTargetFramerateSetResult>({
  name: "runtime.target-framerate.set",
  summary: "Set and read back the current development Player target frame rate.",
  description: "Validate one session-only target frame rate, apply it to the selected development Player, and return fresh Application.targetFrameRate readback. Use -1 for platform default or 0 for unlimited/VSync behavior.",
  group: "runtime",
  handler: "bridge-runtime",
  target: "runtime",
  destructiveHint: false,
  sideEffects: ["runtime-session"],
  supportsDryRun: true,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Set the current Player target to 60 FPS.", argv: ["tools", "run", "runtime.target-framerate.set", "--input", "{\"frameRate\":60}"], input: { frameRate: 60 } }],
  execute() { throw new Error("runtime.target-framerate.set must execute through the bridge-runtime dispatcher"); },
});
