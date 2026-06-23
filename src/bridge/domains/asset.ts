import { assetArgs, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
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
  { group: "asset", command: "rename", bridgeCommand: "asset.rename", buildArgs: assetMutationArgs },
  { group: "asset", command: "search-built-in", bridgeCommand: "asset.search-built-in", buildArgs: assetSearchBuiltInArgs },
  { group: "asset", command: "get-data", bridgeCommand: "asset.get-data", buildArgs: assetGetDataArgs },
  { group: "asset", command: "preview", bridgeCommand: "asset.preview", buildArgs: assetPreviewArgs }
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

function assetSearchBuiltInArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ keyword: positionalTarget ?? stringFlag(flags, "keyword") ?? stringFlag(flags, "search"), type: stringFlag(flags, "type"), pageSize: numberFlag(flags, "page-size") });
}

function assetGetDataArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ assetPath: positionalTarget ?? stringFlag(flags, "asset-path") ?? stringFlag(flags, "path"), maxDepth: numberFlag(flags, "max-depth"), includeFields: stringFlag(flags, "include-fields"), includeProperties: stringFlag(flags, "include-properties") });
}

function assetPreviewArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ assetPath: positionalTarget ?? stringFlag(flags, "asset-path") ?? stringFlag(flags, "path") });
}
