import { boolFlag, compactArgs, floatFlag, numberFlag, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const prefabBridgeSpecs: BridgeCommandSpec[] = [
  { group: "prefab", command: "info", bridgeCommand: "catalog.editor.run", toolName: "prefab.info", buildArgs: prefabArgs },
  { group: "prefab", command: "hierarchy", bridgeCommand: "catalog.editor.run", toolName: "prefab.hierarchy", buildArgs: prefabArgs },
  { group: "prefab", command: "create", bridgeCommand: "catalog.editor.run", toolName: "prefab.create", buildArgs: prefabArgs, destructiveHint: true },
  { group: "prefab", command: "modify", bridgeCommand: "catalog.editor.run", toolName: "prefab.modify", buildArgs: prefabArgs, destructiveHint: true },
  { group: "prefab", command: "overrides-apply", bridgeCommand: "catalog.editor.run", toolName: "prefab.overrides-apply", buildArgs: prefabOverridesArgs, destructiveHint: true, supportsDryRun: true },
  { group: "prefab", command: "overrides-revert", bridgeCommand: "catalog.editor.run", toolName: "prefab.overrides-revert", buildArgs: prefabOverridesArgs, destructiveHint: true, supportsDryRun: true },
  { group: "prefab", command: "variant-create", bridgeCommand: "catalog.editor.run", toolName: "prefab.variant-create", buildArgs: prefabVariantArgs, destructiveHint: true, supportsDryRun: true },
  { group: "prefab", command: "stage-open", bridgeCommand: "catalog.editor.run", toolName: "prefab.stage-open", buildArgs: prefabArgs },
  { group: "prefab", command: "stage-save", bridgeCommand: "catalog.editor.run", toolName: "prefab.stage-save", buildArgs: prefabArgs, destructiveHint: true },
  { group: "prefab", command: "stage-close", bridgeCommand: "catalog.editor.run", toolName: "prefab.stage-close", buildArgs: prefabArgs },
  { group: "prefab", command: "instantiate", bridgeCommand: "catalog.editor.run", toolName: "prefab.instantiate", buildArgs: prefabInstantiateArgs, supportsDryRun: true },
  { group: "prefab", command: "unpack", bridgeCommand: "catalog.editor.run", toolName: "prefab.unpack", buildArgs: prefabUnpackArgs, supportsDryRun: true }
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
    return compactArgs({ path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path") });
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

// 显式保留 override instance selector，确保 static fallback 与 live registry 使用同一输入契约。
function prefabOverridesArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    instance: positionalTarget ?? stringFlag(flags, "instance") ?? stringField(flags, "instance")
  });
}

// variant 的 base/path 都是业务 identity，不能退化为通用 prefab path 参数。
function prefabVariantArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    base: positionalTarget ?? stringFlag(flags, "base") ?? stringField(flags, "base"),
    path: stringFlag(flags, "path") ?? stringField(flags, "path")
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
