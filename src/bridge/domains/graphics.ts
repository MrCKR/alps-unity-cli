import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const graphicsBridgeSpecs: BridgeCommandSpec[] = [
  { group: "graphics", command: "quality", bridgeCommand: "graphics.quality", buildArgs: graphicsArgs },
  { group: "graphics", command: "settings", bridgeCommand: "graphics.settings", buildArgs: graphicsArgs },
  { group: "graphics", command: "screenshot", bridgeCommand: "graphics.screenshot", buildArgs: graphicsArgs },
  { group: "graphics", command: "pipeline", bridgeCommand: "graphics.pipeline", buildArgs: graphicsArgs },
  { group: "graphics", command: "volume", bridgeCommand: "graphics.volume", buildArgs: graphicsArgs },
  { group: "graphics", command: "bake-start", bridgeCommand: "graphics.bake-start", buildArgs: graphicsArgs },
  { group: "graphics", command: "bake-cancel", bridgeCommand: "graphics.bake-cancel", buildArgs: graphicsArgs },
  { group: "graphics", command: "bake-status", bridgeCommand: "graphics.bake-status", buildArgs: graphicsArgs },
  { group: "graphics", command: "bake-clear", bridgeCommand: "graphics.bake-clear", buildArgs: graphicsArgs },
  { group: "graphics", command: "bake-reflection-probe", bridgeCommand: "graphics.bake-reflection-probe", buildArgs: graphicsArgs },
  { group: "graphics", command: "bake-settings", bridgeCommand: "graphics.bake-settings", buildArgs: graphicsArgs },
  { group: "graphics", command: "light-probe-group", bridgeCommand: "graphics.light-probe-group", buildArgs: graphicsArgs },
  { group: "graphics", command: "reflection-probe", bridgeCommand: "graphics.reflection-probe", buildArgs: graphicsArgs },
  { group: "graphics", command: "probe-positions", bridgeCommand: "graphics.probe-positions", buildArgs: graphicsArgs },
  { group: "graphics", command: "stats", bridgeCommand: "graphics.stats", buildArgs: graphicsArgs },
  { group: "graphics", command: "stats-counters", bridgeCommand: "graphics.stats-counters", buildArgs: graphicsArgs },
  { group: "graphics", command: "stats-memory", bridgeCommand: "graphics.stats-memory", buildArgs: graphicsArgs },
  { group: "graphics", command: "scene-debug", bridgeCommand: "graphics.scene-debug", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-get", bridgeCommand: "graphics.skybox-get", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-set-material", bridgeCommand: "graphics.skybox-set-material", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-set-properties", bridgeCommand: "graphics.skybox-set-properties", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-set-ambient", bridgeCommand: "graphics.skybox-set-ambient", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-set-fog", bridgeCommand: "graphics.skybox-set-fog", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-set-reflection", bridgeCommand: "graphics.skybox-set-reflection", buildArgs: graphicsArgs },
  { group: "graphics", command: "skybox-set-sun", bridgeCommand: "graphics.skybox-set-sun", buildArgs: graphicsArgs },
  { group: "graphics", command: "pipeline-settings", bridgeCommand: "graphics.pipeline-settings", buildArgs: graphicsArgs },
  { group: "graphics", command: "pipeline-quality", bridgeCommand: "graphics.pipeline-quality", buildArgs: graphicsArgs },
  { group: "graphics", command: "pipeline-info", bridgeCommand: "graphics.pipeline-info", buildArgs: graphicsArgs }
];

function graphicsArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const s = structuredInput(flags);
  if (command === "graphics.screenshot") {
    return compactArgs({ ...screenshotArgs(flags, positionalTarget), includeDiagnostics: boolFlag(flags, "include-diagnostics") ?? boolField(s, "includeDiagnostics") });
  }
  if (command === "graphics.pipeline") {
    return queryArgs(flags);
  }
  if (command === "graphics.volume") {
    return compactArgs({ ...queryArgs(flags), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringField(s, "target") });
  }
  if (command === "graphics.stats" || command === "graphics.stats-counters" || command === "graphics.stats-memory" || command === "graphics.pipeline-info" || command === "graphics.bake-status") {
    return queryArgs(flags);
  }
  if (command === "graphics.bake-start" || command === "graphics.bake-cancel") {
    return compactArgs({ ...queryArgs(flags) });
  }
  if (command === "graphics.bake-clear") {
    return compactArgs({ bakedData: boolFlag(flags, "baked-data") ?? boolField(s, "bakedData"), realtimeData: boolFlag(flags, "realtime-data") ?? boolField(s, "realtimeData") });
  }
  if (command === "graphics.bake-reflection-probe") {
    return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringField(s, "target") });
  }
  if (command === "graphics.bake-settings") {
    return compactArgs({
      scene: stringFlag(flags, "scene") ?? stringField(s, "scene"),
      lightingMode: stringFlag(flags, "lighting-mode") ?? stringField(s, "lightingMode"),
      bakedGI: boolFlag(flags, "baked-gi") ?? boolField(s, "bakedGI"),
      realtimeGI: boolFlag(flags, "realtime-gi") ?? boolField(s, "realtimeGI"),
      directSamples: numberFlag(flags, "direct-samples") ?? numberField(s, "directSamples"),
      indirectSamples: numberFlag(flags, "indirect-samples") ?? numberField(s, "indirectSamples"),
      environmentSamples: numberFlag(flags, "environment-samples") ?? numberField(s, "environmentSamples"),
      bounces: numberFlag(flags, "bounces") ?? numberField(s, "bounces"),
      filtering: stringFlag(flags, "filtering") ?? stringField(s, "filtering")
    });
  }
  if (command === "graphics.light-probe-group") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(s, "target"),
      create: boolFlag(flags, "create") ?? boolField(s, "create"),
      positions: stringFlag(flags, "positions") ?? stringField(s, "positions")
    });
  }
  if (command === "graphics.reflection-probe") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(s, "target"),
      create: boolFlag(flags, "create") ?? boolField(s, "create"),
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      resolution: numberFlag(flags, "resolution") ?? numberField(s, "resolution"),
      bounces: numberFlag(flags, "bounces") ?? numberField(s, "bounces"),
      hdr: boolFlag(flags, "hdr") ?? boolField(s, "hdr"),
      boxSize: stringFlag(flags, "box-size") ?? stringField(s, "boxSize"),
      boxOffset: stringFlag(flags, "box-offset") ?? stringField(s, "boxOffset")
    });
  }
  if (command === "graphics.probe-positions") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(s, "target"),
      type: stringFlag(flags, "type") ?? stringField(s, "type"),
      positions: stringFlag(flags, "positions") ?? stringField(s, "positions")
    });
  }
  if (command === "graphics.scene-debug") {
    return compactArgs({
      shadingMode: stringFlag(flags, "shading-mode") ?? stringField(s, "shadingMode"),
      drawMode: stringFlag(flags, "draw-mode") ?? stringField(s, "drawMode")
    });
  }
  if (command === "graphics.skybox-get") {
    return queryArgs(flags);
  }
  if (command === "graphics.skybox-set-material") {
    return compactArgs({ material: positionalTarget ?? stringFlag(flags, "material") ?? stringField(s, "material") });
  }
  if (command === "graphics.skybox-set-properties") {
    return compactArgs({ property: stringFlag(flags, "property") ?? stringField(s, "property"), value: stringFlag(flags, "value") ?? stringField(s, "value") });
  }
  if (command === "graphics.skybox-set-ambient") {
    return compactArgs({
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      intensity: floatFlag(flags, "intensity") ?? numberField(s, "intensity"),
      color: stringFlag(flags, "color") ?? stringField(s, "color"),
      ambientEquatorColor: stringFlag(flags, "ambient-equator-color") ?? stringField(s, "ambientEquatorColor"),
      ambientGroundColor: stringFlag(flags, "ambient-ground-color") ?? stringField(s, "ambientGroundColor"),
      ambientSkyColor: stringFlag(flags, "ambient-sky-color") ?? stringField(s, "ambientSkyColor")
    });
  }
  if (command === "graphics.skybox-set-fog") {
    return compactArgs({
      enabled: boolFlag(flags, "enabled") ?? boolField(s, "enabled"),
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      color: stringFlag(flags, "color") ?? stringField(s, "color"),
      density: floatFlag(flags, "density") ?? numberField(s, "density"),
      startDistance: floatFlag(flags, "start-distance") ?? numberField(s, "startDistance"),
      endDistance: floatFlag(flags, "end-distance") ?? numberField(s, "endDistance")
    });
  }
  if (command === "graphics.skybox-set-reflection") {
    return compactArgs({
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      bounces: numberFlag(flags, "bounces") ?? numberField(s, "bounces"),
      intensity: floatFlag(flags, "intensity") ?? numberField(s, "intensity"),
      resolution: stringFlag(flags, "resolution") ?? stringField(s, "resolution")
    });
  }
  if (command === "graphics.skybox-set-sun") {
    return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(s, "target") });
  }
  if (command === "graphics.pipeline-settings") {
    return compactArgs({ renderPipeline: stringFlag(flags, "render-pipeline") ?? stringField(s, "renderPipeline") });
  }
  if (command === "graphics.pipeline-quality") {
    return compactArgs({ qualityLevel: qualityLevelArg(flags) ?? stringField(s, "qualityLevel") ?? numberField(s, "qualityLevel") });
  }
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
