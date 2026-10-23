import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const lightingBridgeSpecs: BridgeCommandSpec[] = [
  { group: "lighting", command: "bake-start", bridgeCommand: "catalog.editor.run", toolName: "lighting.bake-start", buildArgs: lightingArgs, destructiveHint: true },
  { group: "lighting", command: "bake-cancel", bridgeCommand: "catalog.editor.run", toolName: "lighting.bake-cancel", buildArgs: lightingArgs },
  { group: "lighting", command: "bake-status", bridgeCommand: "catalog.editor.run", toolName: "lighting.bake-status", buildArgs: lightingArgs },
  { group: "lighting", command: "bake-clear", bridgeCommand: "catalog.editor.run", toolName: "lighting.bake-clear", buildArgs: lightingArgs, destructiveHint: true },
  { group: "lighting", command: "bake-reflection-probe", bridgeCommand: "catalog.editor.run", toolName: "lighting.bake-reflection-probe", buildArgs: lightingArgs, destructiveHint: true },
  { group: "lighting", command: "bake-settings", bridgeCommand: "catalog.editor.run", toolName: "lighting.bake-settings", buildArgs: lightingArgs, destructiveHint: true },
  { group: "lighting", command: "light-probe-group", bridgeCommand: "catalog.editor.run", toolName: "lighting.light-probe-group", buildArgs: lightingArgs },
  { group: "lighting", command: "reflection-probe", bridgeCommand: "catalog.editor.run", toolName: "lighting.reflection-probe", buildArgs: lightingArgs },
  { group: "lighting", command: "skybox-get", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-get", buildArgs: lightingArgs },
  { group: "lighting", command: "skybox-set-material", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-set-material", buildArgs: lightingArgs },
  { group: "lighting", command: "skybox-set-properties", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-set-properties", buildArgs: lightingArgs, destructiveHint: true },
  { group: "lighting", command: "skybox-set-ambient", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-set-ambient", buildArgs: lightingArgs },
  { group: "lighting", command: "skybox-set-fog", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-set-fog", buildArgs: lightingArgs },
  { group: "lighting", command: "skybox-set-reflection", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-set-reflection", buildArgs: lightingArgs },
  { group: "lighting", command: "skybox-set-sun", bridgeCommand: "catalog.editor.run", toolName: "lighting.skybox-set-sun", buildArgs: lightingArgs }
];

function lightingArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const s = structuredInput(flags);
  if (command === "lighting.bake-status" || command === "lighting.skybox-get") {
    return queryArgs(flags);
  }
  if (command === "lighting.bake-start" || command === "lighting.bake-cancel") {
    return compactArgs({ ...queryArgs(flags) });
  }
  if (command === "lighting.bake-clear") {
    return compactArgs({ bakedData: boolFlag(flags, "baked-data") ?? boolField(s, "bakedData"), realtimeData: boolFlag(flags, "realtime-data") ?? boolField(s, "realtimeData") });
  }
  if (command === "lighting.bake-reflection-probe") {
    return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringField(s, "target") });
  }
  if (command === "lighting.bake-settings") {
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
  if (command === "lighting.light-probe-group") {
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(s, "target"),
      create: boolFlag(flags, "create") ?? boolField(s, "create"),
      positions: stringFlag(flags, "positions") ?? stringField(s, "positions")
    });
  }
  if (command === "lighting.reflection-probe") {
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
  if (command === "lighting.skybox-set-material") {
    return compactArgs({ material: positionalTarget ?? stringFlag(flags, "material") ?? stringField(s, "material") });
  }
  if (command === "lighting.skybox-set-properties") {
    return compactArgs({ property: stringFlag(flags, "property") ?? stringField(s, "property"), value: stringFlag(flags, "value") ?? stringField(s, "value") });
  }
  if (command === "lighting.skybox-set-ambient") {
    return compactArgs({
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      intensity: floatFlag(flags, "intensity") ?? numberField(s, "intensity"),
      color: stringFlag(flags, "color") ?? stringField(s, "color"),
      ambientEquatorColor: stringFlag(flags, "ambient-equator-color") ?? stringField(s, "ambientEquatorColor"),
      ambientGroundColor: stringFlag(flags, "ambient-ground-color") ?? stringField(s, "ambientGroundColor"),
      ambientSkyColor: stringFlag(flags, "ambient-sky-color") ?? stringField(s, "ambientSkyColor")
    });
  }
  if (command === "lighting.skybox-set-fog") {
    return compactArgs({
      enabled: boolFlag(flags, "enabled") ?? boolField(s, "enabled"),
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      color: stringFlag(flags, "color") ?? stringField(s, "color"),
      density: floatFlag(flags, "density") ?? numberField(s, "density"),
      startDistance: floatFlag(flags, "start-distance") ?? numberField(s, "startDistance"),
      endDistance: floatFlag(flags, "end-distance") ?? numberField(s, "endDistance")
    });
  }
  if (command === "lighting.skybox-set-reflection") {
    return compactArgs({
      mode: stringFlag(flags, "mode") ?? stringField(s, "mode"),
      bounces: numberFlag(flags, "bounces") ?? numberField(s, "bounces"),
      intensity: floatFlag(flags, "intensity") ?? numberField(s, "intensity"),
      resolution: stringFlag(flags, "resolution") ?? stringField(s, "resolution")
    });
  }
  // lighting.skybox-set-sun
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(s, "target") });
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
