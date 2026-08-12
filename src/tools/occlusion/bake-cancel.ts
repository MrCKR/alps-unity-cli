import { defineTool } from "../define.js";
import { cancelOcclusionBake, type OcclusionCancelResult } from "./live.js";

export interface OcclusionBakeCancelRequest {
  /** Optional exact operation identity returned by occlusion.bake-status. */
  operationId?: string;
}

export default defineTool<OcclusionBakeCancelRequest, OcclusionCancelResult>({
  name: "occlusion.bake-cancel",
  summary: "Cancel the frozen queued Occlusion bake without stopping unrelated native work.",
  description: "Freezes the supplied operationId directly; when omitted, reads and freezes the active Occlusion coordinator operation before the effect barrier. Queued work stops immediately and returns cancelled=true after its durable cancelled terminal. Running native generation cannot be attributed to one request because Unity exposes only global state, so this tool does not call the global native cancel API and returns cancelled=false with the current status. An idle coordinator is also a no-op.",
  group: "occlusion",
  target: "editor",
  destructiveHint: false,
  timeoutMs: 60_000,
  playModeSupport: "supported",
  sideEffects: ["occlusion-bake-cancel"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Cancel the current tracked Occlusion bake.", argv: ["tools", "run", "occlusion.bake-cancel"] },
    { description: "Cancel one exact operation.", argv: ["tools", "run", "occlusion.bake-cancel", "--input", "{\"operationId\":\"occlusion-...\"}"] },
  ],
  execute(request, context) { return cancelOcclusionBake(request.operationId, context); },
});
