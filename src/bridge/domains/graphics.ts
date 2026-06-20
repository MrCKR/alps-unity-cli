import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

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
  if (command === "graphics.screenshot") {
    return compactArgs({ ...screenshotArgs(flags, positionalTarget), includeDiagnostics: boolFlag(flags, "include-diagnostics") });
  }
  if (command === "graphics.pipeline") {
    return queryArgs(flags);
  }
  if (command === "graphics.volume") {
    return compactArgs({ ...queryArgs(flags), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
  }
  if (command === "graphics.stats" || command === "graphics.stats-counters" || command === "graphics.stats-memory" || command === "graphics.pipeline-info" || command === "graphics.bake-status") {
    return queryArgs(flags);
  }
  if (command === "graphics.bake-start" || command === "graphics.bake-cancel") {
    return compactArgs({ ...queryArgs(flags) });
  }
  if (command === "graphics.bake-clear") {
    return compactArgs({ bakedData: boolFlag(flags, "baked-data"), realtimeData: boolFlag(flags, "realtime-data") });
  }
  if (command === "graphics.bake-reflection-probe") {
    return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") });
  }
  if (command === "graphics.bake-settings") {
    return compactArgs({
      scene: stringFlag(flags, "scene"),
      lightingMode: stringFlag(flags, "lighting-mode"),
      bakedGI: boolFlag(flags, "baked-gi"),
      realtimeGI: boolFlag(flags, "realtime-gi"),
      directSamples: numberFlag(flags, "direct-samples"),
      indirectSamples: numberFlag(flags, "indirect-samples"),
      environmentSamples: numberFlag(flags, "environment-samples"),
      bounces: numberFlag(flags, "bounces"),
      filtering: stringFlag(flags, "filtering")
    });
  }
  if (command === "graphics.light-probe-group") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target"),
      create: boolFlag(flags, "create"),
      positions: stringFlag(flags, "positions")
    });
  }
  if (command === "graphics.reflection-probe") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target"),
      create: boolFlag(flags, "create"),
      mode: stringFlag(flags, "mode"),
      resolution: numberFlag(flags, "resolution"),
      bounces: numberFlag(flags, "bounces"),
      hdr: boolFlag(flags, "hdr"),
      boxSize: stringFlag(flags, "box-size"),
      boxOffset: stringFlag(flags, "box-offset")
    });
  }
  if (command === "graphics.probe-positions") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target"),
      type: stringFlag(flags, "type"),
      positions: stringFlag(flags, "positions")
    });
  }
  if (command === "graphics.scene-debug") {
    return compactArgs({
      shadingMode: stringFlag(flags, "shading-mode"),
      drawMode: stringFlag(flags, "draw-mode")
    });
  }
  if (command === "graphics.skybox-get") {
    return queryArgs(flags);
  }
  if (command === "graphics.skybox-set-material") {
    return compactArgs({ material: positionalTarget ?? stringFlag(flags, "material") });
  }
  if (command === "graphics.skybox-set-properties") {
    return compactArgs({ property: stringFlag(flags, "property"), value: stringFlag(flags, "value") });
  }
  if (command === "graphics.skybox-set-ambient") {
    return compactArgs({
      mode: stringFlag(flags, "mode"),
      intensity: floatFlag(flags, "intensity"),
      color: stringFlag(flags, "color"),
      ambientEquatorColor: stringFlag(flags, "ambient-equator-color"),
      ambientGroundColor: stringFlag(flags, "ambient-ground-color"),
      ambientSkyColor: stringFlag(flags, "ambient-sky-color")
    });
  }
  if (command === "graphics.skybox-set-fog") {
    return compactArgs({
      enabled: boolFlag(flags, "enabled"),
      mode: stringFlag(flags, "mode"),
      color: stringFlag(flags, "color"),
      density: floatFlag(flags, "density"),
      startDistance: floatFlag(flags, "start-distance"),
      endDistance: floatFlag(flags, "end-distance")
    });
  }
  if (command === "graphics.skybox-set-reflection") {
    return compactArgs({
      mode: stringFlag(flags, "mode"),
      bounces: numberFlag(flags, "bounces"),
      intensity: floatFlag(flags, "intensity"),
      resolution: stringFlag(flags, "resolution")
    });
  }
  if (command === "graphics.skybox-set-sun") {
    return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") });
  }
  if (command === "graphics.pipeline-settings") {
    return compactArgs({ renderPipeline: stringFlag(flags, "render-pipeline") });
  }
  if (command === "graphics.pipeline-quality") {
    return compactArgs({ qualityLevel: numberFlag(flags, "quality-level") ?? numberFlag(flags, "level") });
  }
  return compactArgs({
    ...queryArgs(flags),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value"),
    qualityLevel: numberFlag(flags, "quality-level") ?? numberFlag(flags, "level"),
    vSyncCount: numberFlag(flags, "v-sync-count"),
    antiAliasing: numberFlag(flags, "anti-aliasing"),
    shadowDistance: floatFlag(flags, "shadow-distance"),
    masterTextureLimit: numberFlag(flags, "master-texture-limit"),
    lodBias: floatFlag(flags, "lod-bias"),
    maximumLODLevel: numberFlag(flags, "maximum-lod-level"),
    anisotropicFiltering: stringFlag(flags, "anisotropic-filtering"),
    shadows: stringFlag(flags, "shadows"),
    shadowResolution: stringFlag(flags, "shadow-resolution"),
    realtimeReflectionProbes: boolFlag(flags, "realtime-reflection-probes"),
    softParticles: boolFlag(flags, "soft-particles"),
    renderPipeline: stringFlag(flags, "render-pipeline")
  });
}
