import { defineTool } from "../define.js";
import { readOcclusionBakeStatus, type OcclusionBakeStatusResult } from "./live.js";

export interface OcclusionBakeStatusRequest { }

export default defineTool<OcclusionBakeStatusRequest, OcclusionBakeStatusResult>({
  name: "occlusion.bake-status",
  summary: "Read the current session's active or most recent tracked Occlusion bake state.",
  description: "Read-only: returns a bounded process-session snapshot from the Occlusion-only coordinator, including exact operation identity, public phase, target count, duration, fresh effective settings and Umbra data size. It does not acquire the mutation lease or inspect another Editor process lifetime.",
  group: "occlusion",
  target: "editor",
  destructiveHint: false,
  timeoutMs: 30_000,
  playModeSupport: "supported",
  sideEffects: [],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [{ description: "Read Occlusion bake status.", argv: ["tools", "run", "occlusion.bake-status"] }],
  execute(_request, context) { return readOcclusionBakeStatus(context); },
});
