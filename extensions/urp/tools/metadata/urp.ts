import type { ToolMetadata } from "../types.js";
import { objSchema } from "../schemas.js";

/**
 * URP (Universal Render Pipeline) extension tools.
 *
 * Critical reference notes:
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

const URP_PACKAGE = "com.unity.render-pipelines.universal";

/** Common fields shared by all URP extension tools. */
const URP_COMMON = {
  group: "urp",
  target: "editor" as const,
  requiresPackages: [URP_PACKAGE],
};

export const URP_TOOLS: ToolMetadata[] = [
  {
    ...URP_COMMON,
    name: "urp.feature",
    summary: "List, add, remove, toggle or configure URP Renderer Features on the active Universal Render Pipeline asset.",
    description: "List, add, remove, toggle or configure URP Renderer Features on the active Universal Render Pipeline asset. Requires URP (com.unity.render-pipelines.universal) to be the active render pipeline.",
    inputSchema: objSchema({
      action: { type: "string", enum: ["list", "add", "remove", "toggle", "configure"] },
      featureType: { type: "string" },
      index: { type: "integer" },
      enabled: { type: "boolean" },
      property: { type: "string" },
      value: { type: "string" },
      rendererIndex: { type: "integer" }
    }),
    destructiveHint: true,
    examples: [
      { description: "List all renderer features", argv: ["tools", "run", "urp.feature", "--action", "list"] },
      { description: "Add a ScreenSpaceAmbientOcclusion feature", argv: ["tools", "run", "urp.feature", "--action", "add", "--feature-type", "ScreenSpaceAmbientOcclusion"] }
    ]
  },
  {
    ...URP_COMMON,
    name: "urp.volume",
    summary: "List, create or configure URP Volume components and their profile effects.",
    description: "List, create or configure URP Volume components and their profile effects. Requires URP (com.unity.render-pipelines.universal) to be the active render pipeline.",
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
    destructiveHint: false,
    examples: [
      { description: "List URP volumes in scene", argv: ["tools", "run", "urp.volume", "--action", "list"] },
      { description: "Create a global URP volume", argv: ["tools", "run", "urp.volume", "--action", "create", "--is-global", "--name", "GlobalVolume"] }
    ]
  },
  {
    ...URP_COMMON,
    name: "urp.settings",
    summary: "Get or set Universal Render Pipeline asset settings (renderScale, supportsHDR, msaaSampleCount, shadowDistance, etc.).",
    description: "Get or set Universal Render Pipeline asset settings (renderScale, supportsHDR, msaaSampleCount, shadowDistance, etc.). Requires URP (com.unity.render-pipelines.universal) to be the active render pipeline.",
    inputSchema: objSchema({
      property: { type: "string" },
      value: { type: "string" }
    }),
    destructiveHint: false,
    examples: [
      { description: "Read URP pipeline settings", argv: ["tools", "run", "urp.settings"] },
      { description: "Set render scale", argv: ["tools", "run", "urp.settings", "--property", "renderScale", "--value", "1.5"] }
    ]
  }
];
