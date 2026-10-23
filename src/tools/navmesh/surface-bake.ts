import { defineTool } from "../define.js";
import { runNavMeshBake, type NavMeshBakeResult } from "./live.js";

export interface NavMeshSurfaceBakeRequest { }

export default defineTool<NavMeshSurfaceBakeRequest, NavMeshBakeResult>({
  name: "navmesh.surface-bake",
  summary: "Bake all active NavMeshSurface components and prove each generated data asset.",
  description: "Requires com.unity.ai.navigation. Uses the package's registered NavMeshAssetManager editor API through a package-optional reflection boundary. Every target scene must already be saved and clean; terminal proof saves only those frozen scenes and returns scene plus fresh .asset GUID/localId/hash proofs. Dry-run validates the exact surface set without baking.",
  group: "navmesh",
  target: "editor",
  destructiveHint: true,
  supportsDryRun: true,
  timeoutMs: 600_000,
  playModeSupport: "editor-only",
  sideEffects: ["navmesh-surface-bake", "asset-write", "scene-modify"],
  permissions: ["project-asset-write", "scene-modify"],
  requiresPackages: ["com.unity.ai.navigation"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Bake all active surfaces.", argv: ["tools", "run", "navmesh.surface-bake"] },
    { description: "Validate the surface bake plan.", argv: ["tools", "run", "navmesh.surface-bake", "--dry-run"] }
  ],
  execute(_request, context) { return runNavMeshBake("surfaces", context); }
});
