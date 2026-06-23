import { boolFlag, compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const buildBridgeSpecs: BridgeCommandSpec[] = [
  { group: "build", command: "scenes", bridgeCommand: "build.scenes", buildArgs: buildScenesArgs },
  { group: "build", command: "platform", bridgeCommand: "build.platform", buildArgs: buildPlatformArgs },
  { group: "build", command: "settings", bridgeCommand: "build.settings", buildArgs: ({ flags }) => compactArgs({}) }
];

function buildScenesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ action: stringFlag(flags, "action"), path: positionalTarget ?? stringFlag(flags, "path") });
}

function buildPlatformArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ action: stringFlag(flags, "action"), target: positionalTarget ?? stringFlag(flags, "target"), dryRun: boolFlag(flags, "dry-run") });
}
