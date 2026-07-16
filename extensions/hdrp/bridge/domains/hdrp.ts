import { boolFlag, compactArgs, floatFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const hdrpBridgeSpecs: BridgeCommandSpec[] = markToolsOnly([
  { group: "hdrp", command: "volume-list", bridgeCommand: "project-tool.run", toolName: "hdrp.volume.list", buildArgs: emptyArgs },
  { group: "hdrp", command: "volume-create", bridgeCommand: "project-tool.run", toolName: "hdrp.volume.create", buildArgs: volumeCreateArgs },
  { group: "hdrp", command: "volume-info", bridgeCommand: "project-tool.run", toolName: "hdrp.volume.info", buildArgs: volumeTargetArgs },
  { group: "hdrp", command: "volume-add-effect", bridgeCommand: "project-tool.run", toolName: "hdrp.volume.add-effect", buildArgs: volumeEffectArgs },
  { group: "hdrp", command: "volume-set", bridgeCommand: "project-tool.run", toolName: "hdrp.volume.set", buildArgs: volumeSetArgs },
  { group: "hdrp", command: "settings-get", bridgeCommand: "project-tool.run", toolName: "hdrp.settings.get", buildArgs: emptyArgs },
  { group: "hdrp", command: "settings-set", bridgeCommand: "project-tool.run", toolName: "hdrp.settings.set", buildArgs: hdrpSettingsArgs, destructiveHint: true, supportsDryRun: true }
]);

function markToolsOnly(specs: BridgeCommandSpec[]): BridgeCommandSpec[] {
  return specs.map((spec) => ({ ...spec, toolsOnly: true }));
}

function emptyArgs(): Record<string, unknown> { return {}; }

function volumeTargetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target") });
}

function volumeCreateArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ name: stringFlag(flags, "name"), isGlobal: boolFlag(flags, "is-global"), priority: floatFlag(flags, "priority"), weight: floatFlag(flags, "weight") });
}

function volumeEffectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target"), effectType: stringFlag(flags, "effect-type") ?? stringFlag(flags, "type") });
}

function volumeSetArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ target: positionalTarget ?? stringFlag(flags, "target"), effectType: stringFlag(flags, "effect-type") ?? stringFlag(flags, "type"), property: stringFlag(flags, "property"), value: stringFlag(flags, "value") });
}

function hdrpSettingsArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    property: stringFlag(flags, "property"),
    value: stringFlag(flags, "value")
  });
}
