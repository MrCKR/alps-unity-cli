import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const prefabBridgeSpecs: BridgeCommandSpec[] = [
  { group: "prefab", command: "info", bridgeCommand: "prefab.info", buildArgs: prefabArgs },
  { group: "prefab", command: "hierarchy", bridgeCommand: "prefab.hierarchy", buildArgs: prefabArgs },
  { group: "prefab", command: "create", bridgeCommand: "prefab.create", buildArgs: prefabArgs },
  { group: "prefab", command: "modify", bridgeCommand: "prefab.modify", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-open", bridgeCommand: "prefab.stage-open", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-save", bridgeCommand: "prefab.stage-save", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-close", bridgeCommand: "prefab.stage-close", buildArgs: prefabArgs },
  { group: "prefab", command: "instantiate", bridgeCommand: "prefab.instantiate", buildArgs: prefabInstantiateArgs, supportsDryRun: true },
  { group: "prefab", command: "unpack", bridgeCommand: "prefab.unpack", buildArgs: prefabUnpackArgs }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（path/name/addChild 等）
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

function stringField(flags: BridgeFlags, key: string): string | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "string" ? value : undefined;
}

function boolField(flags: BridgeFlags, key: string): boolean | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "boolean" ? value : undefined;
}

function numberField(flags: BridgeFlags, key: string): number | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "number" ? value : undefined;
}

function prefabArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "prefab.create") {
    return compactArgs({
      source: positionalTarget ?? stringFlag(flags, "source") ?? stringFlag(flags, "target") ?? stringField(flags, "source"),
      path: stringFlag(flags, "path") ?? stringFlag(flags, "destination") ?? stringFlag(flags, "to") ?? stringField(flags, "path"),
      overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite")
    });
  }
  if (command === "prefab.modify") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "prefab") ?? stringField(flags, "path"),
      name: stringFlag(flags, "name") ?? stringField(flags, "name"),
      addChild: stringFlag(flags, "add-child") ?? stringField(flags, "addChild"),
      removeChild: stringFlag(flags, "remove-child") ?? stringField(flags, "removeChild"),
      overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite")
    });
  }
  if (command === "prefab.stage-save" || command === "prefab.stage-close") {
    return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"), save: boolFlag(flags, "save") ?? boolField(flags, "save") });
  }
  return compactArgs({
    ...queryArgs(flags),
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "prefab") ?? stringField(flags, "path"),
    parent: stringFlag(flags, "parent") ?? stringField(flags, "parent"),
    query: stringFlag(flags, "query") ?? stringField(flags, "query"),
    nameFilter: stringFlag(flags, "name-filter") ?? stringField(flags, "nameFilter"),
    componentType: stringFlag(flags, "component-type") ?? stringField(flags, "componentType"),
    includeSummaryDetails: boolFlag(flags, "include-summary-details") ?? boolField(flags, "includeSummaryDetails"),
    pageSize: numberFlag(flags, "page-size") ?? numberField(flags, "pageSize"),
    cursor: stringFlag(flags, "cursor") ?? stringField(flags, "cursor")
  });
}

function prefabInstantiateArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"),
    parent: stringFlag(flags, "parent") ?? stringField(flags, "parent"),
    name: stringFlag(flags, "name") ?? stringField(flags, "name"),
    x: floatFlag(flags, "x") ?? numberField(flags, "x"),
    y: floatFlag(flags, "y") ?? numberField(flags, "y"),
    z: floatFlag(flags, "z") ?? numberField(flags, "z"),
    dryRun: boolFlag(flags, "dry-run") ?? boolField(flags, "dryRun")
  });
}

function prefabUnpackArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringField(flags, "target"),
    path: stringFlag(flags, "path") ?? stringField(flags, "path"),
    dryRun: boolFlag(flags, "dry-run") ?? boolField(flags, "dryRun")
  });
}
