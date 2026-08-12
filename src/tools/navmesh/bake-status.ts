import { defineTool } from "../define.js";
import { readNavMeshBakeStatus, type NavMeshBakeStatusResult } from "./live.js";

export interface NavMeshBakeStatusRequest { }

export default defineTool<NavMeshBakeStatusRequest, NavMeshBakeStatusResult>({
  name: "navmesh.bake-status",
  summary: "Read the current session's active or most recent tracked NavMesh bake state.",
  description: "Read-only: returns a bounded session-scoped snapshot for the unified legacy/surface bake coordinator, including exact operation identity, public phase, progress, target count, duration, and fresh NavMesh topology. It does not acquire the mutation lease or inspect another Editor process lifetime.",
  group: "navmesh",
  target: "editor",
  destructiveHint: false,
  timeoutMs: 30_000,
  playModeSupport: "supported",
  sideEffects: [],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [{ description: "Read NavMesh bake status.", argv: ["tools", "run", "navmesh.bake-status"] }],
  execute(_request, context) { return readNavMeshBakeStatus(context); }
});
