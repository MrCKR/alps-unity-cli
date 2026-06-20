import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * HDRP (High Definition Render Pipeline) extension tools.
 *
 * Critical reference notes (ALP-505 P8):
 * - Critically references unity-mcp's Graphics/VolumeOps.cs and GraphicsHelpers.cs
 *   for HDRP volume and pipeline operations.
 * - Discarded: unity-mcp routes HDRP features through the graphics domain.
 *   alps-unity-cli gives HDRP its own group (hdrp) to cleanly separate
 *   pipeline-specific tools that only work when HDRP is the active render pipeline.
 * - Adapted for alps-unity-cli: HDRP detection uses GraphicsSettings.currentRenderPipeline
 *   type name checking (same pattern as URP), implemented via FindType + reflection.
 *   Volume operations share the same UnityEngine.Rendering.Volume base types as URP,
 *   but HDRP-specific effects and settings are accessed via HDRP assembly types.
 */
export const HDRP_TOOLS: ToolMetadata[] = [
  {
    name: "hdrp.volume",
    title: "HDRP Volume",
    description: "List, create or configure HDRP Volume components and their profile effects. Requires HDRP (com.unity.render-pipelines.high-definition) to be the active render pipeline.",
    group: "hdrp",
    inputSchema: objSchema({
      target: { type: "string" },
      action: { type: "string", enum: ["list", "create", "add-effect", "set", "info"] },
      effectType: { type: "string" },
      isGlobal: { type: "boolean" },
      priority: { type: "number" },
      weight: { type: "number" },
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "hdrp volume",
    docsTopic: "hdrp",
    requiresPackages: ["com.unity.render-pipelines.high-definition"],
    examples: [
      { description: "List HDRP volumes in scene", argv: ["hdrp", "volume", "--action", "list"] },
      { description: "Create a global HDRP volume", argv: ["hdrp", "volume", "--action", "create", "--is-global", "true", "--name", "GlobalVolume"] }
    ]
  },
  {
    name: "hdrp.settings",
    title: "HDRP Pipeline Settings",
    description: "Get or set High Definition Render Pipeline asset settings. Requires HDRP (com.unity.render-pipelines.high-definition) to be the active render pipeline.",
    group: "hdrp",
    inputSchema: objSchema({
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "hdrp settings",
    docsTopic: "hdrp",
    requiresPackages: ["com.unity.render-pipelines.high-definition"],
    examples: [
      { description: "Read HDRP pipeline settings", argv: ["hdrp", "settings"] },
      { description: "Set shadow distance", argv: ["hdrp", "settings", "--property", "shadowDistance", "--value", "500"] }
    ]
  }
];
