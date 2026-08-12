import { defineTool } from "../define.js";
import { cancelNavMeshBake, type NavMeshCancelResult } from "./live.js";

export interface NavMeshBakeCancelRequest {
  /** Optional exact operation identity returned by navmesh.bake-status. */
  operationId?: string;
}

export default defineTool<NavMeshBakeCancelRequest, NavMeshCancelResult>({
  name: "navmesh.bake-cancel",
  summary: "Request cooperative cancellation for one frozen NavMesh bake.",
  description: "Reads and freezes the active coordinator operation before the effect barrier. Queued work reaches a cancelled terminal immediately. Running work records a cooperative request, waits for the current native operation to end, and does not call legacy or NavMeshSurface native cancel APIs across frames. The call returns cancelled=true only after the same operation reaches its durable cancelled terminal; an idle coordinator is a no-op.",
  group: "navmesh",
  target: "editor",
  destructiveHint: false,
  timeoutMs: 60_000,
  playModeSupport: "supported",
  sideEffects: ["navmesh-bake-cancel"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Cancel the current tracked bake.", argv: ["tools", "run", "navmesh.bake-cancel"] },
    { description: "Cancel one exact operation.", argv: ["tools", "run", "navmesh.bake-cancel", "--input", "{\"operationId\":\"navmesh-...\"}"] }
  ],
  execute(request, context) { return cancelNavMeshBake(request.operationId, context); }
});
