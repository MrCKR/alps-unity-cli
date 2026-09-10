import { assetArgs, boolFlag, compactArgs, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const assetBridgeSpecs: BridgeCommandSpec[] = [
  { group: "asset", command: "search", bridgeCommand: "catalog.editor.run", toolName: "asset.search", buildArgs: assetQueryArgs },
  { group: "asset", command: "info", bridgeCommand: "catalog.editor.run", toolName: "asset.info", buildArgs: assetQueryArgs },
  { group: "asset", command: "create", bridgeCommand: "catalog.editor.run", toolName: "asset.create", buildArgs: assetMutationArgs, destructiveHint: true },
  { group: "asset", command: "import", bridgeCommand: "catalog.editor.run", toolName: "asset.import", buildArgs: assetMutationArgs, destructiveHint: true, supportsDryRun: true },
  { group: "asset", command: "import-settings", bridgeCommand: "catalog.editor.run", toolName: "asset.import-settings", buildArgs: assetQueryArgs },
  { group: "asset", command: "import-settings.set", bridgeCommand: "catalog.editor.run", toolName: "asset.import-settings.set", buildArgs: assetMutationArgs, destructiveHint: true, supportsDryRun: true },
  { group: "asset", command: "write-text", bridgeCommand: "catalog.editor.run", toolName: "asset.write-text", buildArgs: assetMutationArgs, destructiveHint: true },
  { group: "asset", command: "delete", bridgeCommand: "catalog.editor.run", toolName: "asset.delete", buildArgs: assetMutationArgs, destructiveHint: true, supportsDryRun: true },
  { group: "asset", command: "duplicate", bridgeCommand: "catalog.editor.run", toolName: "asset.duplicate", buildArgs: assetMutationArgs, destructiveHint: true },
  { group: "asset", command: "move", bridgeCommand: "catalog.editor.run", toolName: "asset.move", buildArgs: assetMutationArgs, destructiveHint: true },
  { group: "asset", command: "rename", bridgeCommand: "catalog.editor.run", toolName: "asset.rename", buildArgs: assetMutationArgs, destructiveHint: true },
  { group: "asset", command: "search-built-in", bridgeCommand: "catalog.editor.run", toolName: "asset.search-built-in", buildArgs: assetSearchBuiltInArgs },
  { group: "asset", command: "get-data", bridgeCommand: "catalog.editor.run", toolName: "asset.get-data", buildArgs: assetGetDataArgs },
  { group: "asset", command: "get-components", bridgeCommand: "catalog.editor.run", toolName: "asset.get-components", buildArgs: assetGetComponentsArgs },
  { group: "asset", command: "preview", bridgeCommand: "catalog.editor.run", toolName: "asset.preview", buildArgs: assetPreviewArgs }
];

/** 读取 tools run --input 中无法映射为简单 flag 的结构化字段。 */
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

function assetQueryArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    ...queryArgs(flags),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "query") ?? structured.query ?? structured.filter,
    query: stringFlag(flags, "query") ?? positionalTarget ?? structured.query,
    filter: stringFlag(flags, "filter") ?? structured.filter,
    type: stringFlag(flags, "type") ?? structured.type,
    path: stringFlag(flags, "path") ?? structured.path,
    searchInFolders: structured.searchInFolders,
    guid: stringFlag(flags, "guid") ?? structured.guid,
    pageSize: numberFlag(flags, "page-size") ?? structured.pageSize ?? structured.maxResults,
    maxResults: structured.maxResults,
    cursor: numberFlag(flags, "cursor") ?? structured.cursor
  });
}

function assetMutationArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  return assetArgs(command, flags, positionalTarget);
}

function assetSearchBuiltInArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ keyword: positionalTarget ?? stringFlag(flags, "keyword") ?? stringFlag(flags, "search"), type: stringFlag(flags, "type"), pageSize: numberFlag(flags, "page-size") });
}

function assetGetDataArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    assetPath: positionalTarget ?? stringFlag(flags, "asset-path") ?? stringFlag(flags, "path") ?? structured.assetPath,
    path: structured.path,
    maxDepth: numberFlag(flags, "max-depth") ?? structured.maxDepth,
    maxArrayItems: numberFlag(flags, "max-array-items") ?? structured.maxArrayItems,
    includeFields: boolFlag(flags, "include-fields") ?? structured.includeFields,
    includeProperties: boolFlag(flags, "include-properties") ?? structured.includeProperties,
    deepSerialization: boolFlag(flags, "deep-serialization") ?? structured.deepSerialization,
    paths: structured.paths,
    viewQuery: structured.viewQuery,
    namePattern: stringFlag(flags, "name-pattern") ?? structured.namePattern,
    budget: structured.budget
  });
}

function assetGetComponentsArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    assetPath: positionalTarget ?? stringFlag(flags, "asset-path") ?? stringFlag(flags, "path") ?? stringFlag(flags, "target") ?? structured.assetPath ?? structured.path,
    includeInactive: boolFlag(flags, "include-inactive") ?? structured.includeInactive,
    query: stringFlag(flags, "query") ?? structured.query,
    nameFilter: stringFlag(flags, "name-filter") ?? structured.nameFilter,
    pathFilter: stringFlag(flags, "path-filter") ?? structured.pathFilter,
    componentType: stringFlag(flags, "component-type") ?? structured.componentType,
    pageSize: numberFlag(flags, "page-size") ?? structured.pageSize,
    cursor: numberFlag(flags, "cursor") ?? structured.cursor
  });
}

function assetPreviewArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ assetPath: positionalTarget ?? stringFlag(flags, "asset-path") ?? stringFlag(flags, "path") });
}
