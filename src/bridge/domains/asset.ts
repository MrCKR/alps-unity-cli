import { assetArgs, compactArgs, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const assetBridgeSpecs: BridgeCommandSpec[] = [
  { group: "asset", command: "search", bridgeCommand: "asset.search", buildArgs: assetQueryArgs },
  { group: "asset", command: "info", bridgeCommand: "asset.info", buildArgs: assetQueryArgs },
  { group: "asset", command: "create", bridgeCommand: "asset.create", buildArgs: assetMutationArgs },
  { group: "asset", command: "mkdir", bridgeCommand: "asset.create", buildArgs: assetMutationArgs },
  { group: "asset", command: "import", bridgeCommand: "asset.import", buildArgs: assetMutationArgs },
  { group: "asset", command: "modify", bridgeCommand: "asset.modify", buildArgs: assetMutationArgs },
  { group: "asset", command: "delete", bridgeCommand: "asset.delete", buildArgs: assetMutationArgs },
  { group: "asset", command: "duplicate", bridgeCommand: "asset.duplicate", buildArgs: assetMutationArgs },
  { group: "asset", command: "move", bridgeCommand: "asset.move", buildArgs: assetMutationArgs },
  { group: "asset", command: "rename", bridgeCommand: "asset.rename", buildArgs: assetMutationArgs }
];

function assetQueryArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    ...queryArgs(flags),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "query"),
    query: stringFlag(flags, "query") ?? stringFlag(flags, "filter") ?? positionalTarget,
    type: stringFlag(flags, "type"),
    path: stringFlag(flags, "path"),
    guid: stringFlag(flags, "guid")
  });
}

function assetMutationArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  return assetArgs(command, flags, positionalTarget);
}
