import { boolFlag, compactArgs, floatFlag, queryArgs, stringFlag, targetArgs } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const hdrpBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "hdrp", command: "volume", bridgeCommand: "hdrp.volume", buildArgs: hdrpVolumeArgs },
  { group: "hdrp", command: "settings", bridgeCommand: "hdrp.settings", buildArgs: hdrpSettingsArgs }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function hdrpVolumeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...targetArgs(flags, positionalTarget),
    action: stringFlag(flags, "action") ?? stringFlag(flags, "op") ?? "list",
    effectType: stringFlag(flags, "effect-type") ?? stringFlag(flags, "type"),
    isGlobal: boolFlag(flags, "is-global") || undefined,
    priority: floatFlag(flags, "priority"),
    weight: floatFlag(flags, "weight"),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}

function hdrpSettingsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...queryArgs(flags),
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}

