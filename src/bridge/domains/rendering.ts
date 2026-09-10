import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const renderingBridgeSpecs: BridgeCommandSpec[] = [
  { group: "rendering", command: "quality", bridgeCommand: "catalog.editor.run", toolName: "rendering.quality", buildArgs: renderingArgs, destructiveHint: true },
  { group: "rendering", command: "scene-debug", bridgeCommand: "catalog.editor.run", toolName: "rendering.scene-debug", buildArgs: renderingArgs },
  { group: "rendering", command: "stats", bridgeCommand: "catalog.editor.run", toolName: "rendering.stats", buildArgs: renderingArgs },
  { group: "rendering", command: "pipeline.settings", bridgeCommand: "catalog.editor.run", toolName: "rendering.pipeline.settings", buildArgs: renderingArgs, destructiveHint: true },
  { group: "rendering", command: "pipeline.info", bridgeCommand: "catalog.editor.run", toolName: "rendering.pipeline.info", buildArgs: renderingArgs }
];

function renderingArgs({ command, flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const s = structuredInput(flags);
  if (command === "rendering.stats" || command === "rendering.pipeline.info") {
    return queryArgs(flags);
  }
  if (command === "rendering.scene-debug") {
    return compactArgs({
      shadingMode: stringFlag(flags, "shading-mode") ?? stringField(s, "shadingMode"),
      drawMode: stringFlag(flags, "draw-mode") ?? stringField(s, "drawMode")
    });
  }
  if (command === "rendering.pipeline.settings") {
    return compactArgs({ renderPipeline: stringFlag(flags, "render-pipeline") ?? stringField(s, "renderPipeline") });
  }
  // rendering.quality
  return compactArgs({
    ...queryArgs(flags),
    property: stringFlag(flags, "property") ?? stringField(s, "property"),
    value: stringFlag(flags, "value") ?? stringField(s, "value"),
    qualityLevel: qualityLevelArg(flags) ?? stringField(s, "qualityLevel") ?? numberField(s, "qualityLevel"),
    vSyncCount: numberFlag(flags, "v-sync-count") ?? numberField(s, "vSyncCount"),
    antiAliasing: numberFlag(flags, "anti-aliasing") ?? numberField(s, "antiAliasing"),
    shadowDistance: floatFlag(flags, "shadow-distance") ?? numberField(s, "shadowDistance"),
    masterTextureLimit: numberFlag(flags, "master-texture-limit") ?? numberField(s, "masterTextureLimit"),
    lodBias: floatFlag(flags, "lod-bias") ?? numberField(s, "lodBias"),
    maximumLODLevel: numberFlag(flags, "maximum-lod-level") ?? numberField(s, "maximumLODLevel"),
    anisotropicFiltering: stringFlag(flags, "anisotropic-filtering") ?? stringField(s, "anisotropicFiltering"),
    shadows: stringFlag(flags, "shadows") ?? stringField(s, "shadows"),
    shadowResolution: stringFlag(flags, "shadow-resolution") ?? stringField(s, "shadowResolution"),
    realtimeReflectionProbes: boolFlag(flags, "realtime-reflection-probes") ?? boolField(s, "realtimeReflectionProbes"),
    softParticles: boolFlag(flags, "soft-particles") ?? boolField(s, "softParticles"),
    renderPipeline: stringFlag(flags, "render-pipeline") ?? stringField(s, "renderPipeline")
  });
}

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段
function structuredInput(flags: BridgeFlags): Record<string, unknown> {
  const raw = typeof flags.__structuredInput === "string" ? flags.__structuredInput : undefined;
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function stringField(s: Record<string, unknown>, key: string): string | undefined {
  const value = s[key];
  return typeof value === "string" ? value : undefined;
}

function numberField(s: Record<string, unknown>, key: string): number | undefined {
  const value = s[key];
  return typeof value === "number" ? value : undefined;
}

function boolField(s: Record<string, unknown>, key: string): boolean | undefined {
  const value = s[key];
  return typeof value === "boolean" ? value : undefined;
}

function qualityLevelArg(flags: Record<string, string | boolean>): number | string | undefined {
  return numberFlag(flags, "quality-level") ?? numberFlag(flags, "level") ?? stringFlag(flags, "quality-level") ?? stringFlag(flags, "level");
}
