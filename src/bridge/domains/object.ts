import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeFlags, BridgeCommandSpec } from "../types.js";

/**
 * 从 __structuredInput (mergeInputToFlags 产生的 JSON 字符串) 中提取复杂字段。
 * tools run --input '{"pathPatches":[...]}' 会把数组/对象放入 __structuredInput。
 */
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

/** 合并 CLI 标志与结构化输入，构建 object.get-data / object.modify 的 bridge 参数。 */
function objectArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    instanceId: numberFlag(flags, "instance-id") ?? structured.instanceId,
    assetPath: stringFlag(flags, "asset-path") ?? structured.assetPath,
    guid: stringFlag(flags, "guid") ?? structured.guid,
    path: stringFlag(flags, "path") ?? structured.path,
    name: stringFlag(flags, "name") ?? structured.name,
    target: stringFlag(flags, "target") ?? structured.target,
    maxDepth: numberFlag(flags, "max-depth") ?? structured.maxDepth,
    maxArrayItems: numberFlag(flags, "max-array-items") ?? structured.maxArrayItems,
    includeFields: boolFlag(flags, "include-fields") ?? structured.includeFields,
    includeProperties: boolFlag(flags, "include-properties") ?? structured.includeProperties,
    deepSerialization: boolFlag(flags, "deep-serialization") ?? structured.deepSerialization,
    paths: structured.paths,
    viewQuery: structured.viewQuery,
    namePattern: stringFlag(flags, "name-pattern") ?? structured.namePattern,
    budget: structured.budget,
    pathPatches: structured.pathPatches,
    jsonPatch: structured.jsonPatch,
    dryRun: boolFlag(flags, "dry-run")
  });
}

export const objectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "object", command: "get-data", bridgeCommand: "project-tool.run", toolName: "object.get-data", buildArgs: objectArgs },
  { group: "object", command: "modify", bridgeCommand: "project-tool.run", toolName: "object.modify", buildArgs: objectArgs, destructiveHint: true, supportsDryRun: true }
];
