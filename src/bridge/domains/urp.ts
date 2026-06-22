import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const urpBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "urp", command: "feature", bridgeCommand: "urp.feature", buildArgs: urpFeatureArgs },
  { group: "urp", command: "volume", bridgeCommand: "urp.volume", buildArgs: urpVolumeArgs },
  { group: "urp", command: "settings", bridgeCommand: "urp.settings", buildArgs: urpSettingsArgs }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function urpFeatureArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...queryArgs(flags),
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
    ...targetArgs(flags, positionalTarget),
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
    ...queryArgs(flags),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}

