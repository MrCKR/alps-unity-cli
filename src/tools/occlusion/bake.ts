import { defineTool } from "../define.js";
import { runOcclusionBake, type OcclusionBakeResult } from "./live.js";

export interface OcclusionBakeRequest {
  /** Smallest object that occludes others in meters; omit to use the current Unity setting. */
  smallestOccluder?: number;
  /** Smallest visible hole in meters; omit to use the current Unity setting. */
  smallestHole?: number;
  /** Backface threshold in [1,100]; omit to use the current Unity setting. */
  backfaceThreshold?: number;
}

export default defineTool<OcclusionBakeRequest, OcclusionBakeResult>({
  name: "occlusion.bake",
  summary: "Bake Occlusion Culling for loaded saved scenes and return its durable terminal proof.",
  description: "Starts StaticOcclusionCulling.GenerateInBackground under an Occlusion-only session-scoped durable root. Every loaded target scene must already be saved and clean. Effective settings are frozen before dispatch; completion saves only those frozen scenes and returns settings, Umbra data size, GUID/hash/size and dirty=false. Timeout or reload ambiguity returns outcome unknown and must be inspected with occlusion.bake-status. Dry-run validates the complete plan without baking.",
  group: "occlusion",
  target: "editor",
  destructiveHint: true,
  supportsDryRun: true,
  timeoutMs: 600_000,
  playModeSupport: "editor-only",
  sideEffects: ["occlusion-bake", "scene-modify"],
  permissions: ["scene-modify"],
  handler: "hybrid",
  supportStatus: "live-bridge",
  examples: [
    { description: "Bake with current Unity settings.", argv: ["tools", "run", "occlusion.bake"] },
    { description: "Validate explicit settings without baking.", argv: ["tools", "run", "occlusion.bake", "--input", "{\"smallestOccluder\":5,\"smallestHole\":0.25,\"backfaceThreshold\":100}", "--dry-run"] },
  ],
  execute(request, context) { return runOcclusionBake(request, context); },
});
