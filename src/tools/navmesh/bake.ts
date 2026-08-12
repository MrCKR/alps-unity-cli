import { defineTool } from "../define.js";
import { runNavMeshBake, type NavMeshBakeResult } from "./live.js";

export interface NavMeshBakeRequest { }

export default defineTool<NavMeshBakeRequest, NavMeshBakeResult>({
  name: "navmesh.bake",
  summary: "Bake the legacy NavMesh for loaded saved scenes and return its durable terminal readback.",
  description: "Starts UnityEditor.AI.NavMeshBuilder under one session-scoped durable root. Every loaded target scene must already be saved and clean; terminal proof saves only those frozen scenes and returns GUID/hash/size with dirty=false. Timeout or reload ambiguity returns outcome unknown and must be inspected with navmesh.bake-status. Dry-run validates the complete scene plan without baking.",
  group: "navmesh",
  target: "editor",
  destructiveHint: true,
  supportsDryRun: true,
  timeoutMs: 600_000,
  playModeSupport: "editor-only",
  sideEffects: ["navmesh-bake", "scene-modify"],
  permissions: ["scene-modify"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Bake the legacy NavMesh for loaded scenes.", argv: ["tools", "run", "navmesh.bake"] },
    { description: "Validate the legacy bake plan.", argv: ["tools", "run", "navmesh.bake", "--dry-run"] }
  ],
  execute(_request, context) { return runNavMeshBake("legacy", context); }
});
