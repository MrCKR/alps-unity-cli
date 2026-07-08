import { boolFlag, compactArgs, floatFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const urpBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "urp", command: "feature", bridgeCommand: "project-tool.run", toolName: "urp.feature", buildArgs: urpFeatureArgs },
  { group: "urp", command: "volume", bridgeCommand: "project-tool.run", toolName: "urp.volume", buildArgs: urpVolumeArgs },
  { group: "urp", command: "settings", bridgeCommand: "project-tool.run", toolName: "urp.settings", buildArgs: urpSettingsArgs }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function urpFeatureArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? "list",
    featureType: stringFlag(flags, "feature-type") ?? stringFlag(flags, "type"),
    index: numberFlag(flags, "index"),
    enabled: boolFlag(flags, "enabled"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value"),
    rendererIndex: numberFlag(flags, "renderer-index")
  });
}

function urpVolumeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    name: stringFlag(flags, "name"),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? "list",
    effectType: stringFlag(flags, "effect-type") ?? stringFlag(flags, "type"),
    isGlobal: boolFlag(flags, "is-global"),
    priority: floatFlag(flags, "priority"),
    weight: floatFlag(flags, "weight"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}

function urpSettingsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}
