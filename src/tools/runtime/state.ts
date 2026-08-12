import { defineTool } from "../define.js";

export interface RuntimeStateRequest {}
export interface RuntimeSceneState { name: string; buildIndex: number; isLoaded: boolean }
export interface RuntimeFrameState { frameCount: number; realtimeSinceStartup: number; targetFrameRate: number }
export interface RuntimeTimeState { timeScale: number; time: number; unscaledTime: number }
export interface RuntimeScreenState { width: number; height: number; dpi: number; fullScreen: boolean; focused: boolean }
export interface RuntimeMemoryState { totalAllocatedBytes: number; totalReservedBytes: number; totalUnusedReservedBytes: number }
export interface RuntimeStateResult {
  runtimeSessionId: string;
  buildId: string;
  observedAt: string;
  unityVersion: string;
  platform: string;
  productName: string;
  applicationVersion: string;
  scene: RuntimeSceneState;
  frame: RuntimeFrameState;
  time: RuntimeTimeState;
  screen: RuntimeScreenState;
  memory: RuntimeMemoryState;
}

export default defineTool<RuntimeStateRequest, RuntimeStateResult>({
  name: "runtime.state",
  summary: "Read bounded state from the selected development Player session.",
  description: "Read one fresh, session-bound snapshot from the selected Windows development Player, including application, scene, frame, time, screen, and managed runtime memory state.",
  group: "runtime",
  handler: "bridge-runtime",
  target: "runtime",
  destructiveHint: false,
  sideEffects: [],
  supportsDryRun: false,
  playModeSupport: "runtime-only",
  timeoutMs: 10_000,
  examples: [{ description: "Read the current development Player state.", argv: ["tools", "run", "runtime.state", "--input", "{}"], input: {} }],
  execute() { throw new Error("runtime.state must execute through the bridge-runtime dispatcher"); },
});
