import { boolFlag, compactArgs, floatFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const hdrpBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "hdrp", command: "volume", bridgeCommand: "project-tool.run", toolName: "hdrp.volume", buildArgs: hdrpVolumeArgs },
  { group: "hdrp", command: "settings", bridgeCommand: "project-tool.run", toolName: "hdrp.settings", buildArgs: hdrpSettingsArgs }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function hdrpVolumeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name"),
    name: stringFlag(flags, "name"),
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
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}
