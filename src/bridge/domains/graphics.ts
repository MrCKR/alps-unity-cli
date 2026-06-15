import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, screenshotArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const graphicsBridgeSpecs: BridgeCommandSpec[] = [
  { group: "graphics", command: "quality", bridgeCommand: "graphics.quality", buildArgs: graphicsArgs },
  { group: "graphics", command: "settings", bridgeCommand: "graphics.settings", buildArgs: graphicsArgs },
  { group: "graphics", command: "screenshot", bridgeCommand: "graphics.screenshot", buildArgs: graphicsArgs },
  { group: "graphics", command: "pipeline", bridgeCommand: "graphics.pipeline", buildArgs: graphicsArgs },
  { group: "graphics", command: "volume", bridgeCommand: "graphics.volume", buildArgs: graphicsArgs }
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
