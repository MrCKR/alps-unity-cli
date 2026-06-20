import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * URP (Universal Render Pipeline) extension tools.
 *
 * Critical reference notes (ALP-505 P8):
 * - Critically references unity-mcp's Graphics/RendererFeatureOps.cs for renderer
 *   feature management (list, add, remove, configure, toggle, reorder) and
 *   Graphics/VolumeOps.cs for volume operations (create, add/set/remove effects, get info).
 * - Discarded: unity-mcp routes URP/HDRP features through the graphics domain with
 *   action routing. alps-unity-cli gives URP its own group (urp) to cleanly separate
 *   pipeline-specific tools that only work when URP is the active render pipeline.
 * - Adapted for alps-unity-cli: URP detection uses GraphicsSettings.currentRenderPipeline
 *   type name checking (same as unity-mcp's RenderPipelineUtility.GetActivePipeline()),
 *   but implemented via FindType + reflection (no compile-time dependency).
 * - Renderer features accessed via SerializedObject on UniversalRenderPipelineAsset's
 *   m_RendererDataList → ScriptableRendererData's m_RendererFeatures array, mirroring
 *   unity-mcp's approach but without direct type references.
 */
export const URP_TOOLS: ToolMetadata[] = [
  {
    name: "urp.feature",
    title: "URP Renderer Feature",
    description: "List, add, remove, toggle or configure URP Renderer Features on the active Universal Render Pipeline asset. Requires URP (com.unity.render-pipelines.universal) to be the active render pipeline.",
    group: "urp",
    inputSchema: objSchema({
      action: { type: "string", enum: ["list", "add", "remove", "toggle", "configure"] },
      featureType: { type: "string" },
      index: { type: "integer" },
      enabled: { type: "boolean" },
      property: { type: "string" },
      value: { type: "string" },
      rendererIndex: { type: "integer" }
    }),
    readOnlyHint: false,
    destructiveHint: true,
    handler: "hybrid",
    cliCommand: "urp feature",
    docsTopic: "urp",
    requiresPackages: ["com.unity.render-pipelines.universal"],
    examples: [
      { description: "List all renderer features", argv: ["urp", "feature", "--action", "list"] },
      { description: "Add a ScreenSpaceAmbientOcclusion feature", argv: ["urp", "feature", "--action", "add", "--feature-type", "ScreenSpaceAmbientOcclusion"] }
    ]
  },
  {
    name: "urp.volume",
    title: "URP Volume",
    description: "List, create or configure URP Volume components and their profile effects. Requires URP (com.unity.render-pipelines.universal) to be the active render pipeline.",
    group: "urp",
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
    cliCommand: "urp volume",
    docsTopic: "urp",
    requiresPackages: ["com.unity.render-pipelines.universal"],
    examples: [
      { description: "List URP volumes in scene", argv: ["urp", "volume", "--action", "list"] },
      { description: "Create a global URP volume", argv: ["urp", "volume", "--action", "create", "--is-global", "--name", "GlobalVolume"] }
    ]
  },
  {
    name: "urp.settings",
    title: "URP Pipeline Settings",
    description: "Get or set Universal Render Pipeline asset settings (renderScale, supportsHDR, msaaSampleCount, shadowDistance, etc.). Requires URP (com.unity.render-pipelines.universal) to be the active render pipeline.",
    group: "urp",
    inputSchema: objSchema({
      property: { type: "string" },
      value: { type: "string" }
    }),
    readOnlyHint: false,
    destructiveHint: false,
    handler: "hybrid",
    cliCommand: "urp settings",
    docsTopic: "urp",
    requiresPackages: ["com.unity.render-pipelines.universal"],
    examples: [
      { description: "Read URP pipeline settings", argv: ["urp", "settings"] },
      { description: "Set render scale", argv: ["urp", "settings", "--property", "renderScale", "--value", "1.5"] }
    ]
  }
];
