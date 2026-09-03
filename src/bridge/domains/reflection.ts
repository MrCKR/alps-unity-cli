import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeFlags, BridgeCommandSpec } from "../types.js";

/** 从 __structuredInput 中提取复杂字段。 */
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

function reflectionFindArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    typeName: stringFlag(flags, "type-name") ?? structured.typeName,
    methodName: stringFlag(flags, "method-name") ?? structured.methodName,
    includePrivate: boolFlag(flags, "include-private") ?? structured.includePrivate,
    includeStatic: boolFlag(flags, "include-static") ?? structured.includeStatic,
    includeInstance: boolFlag(flags, "include-instance") ?? structured.includeInstance,
    pageSize: numberFlag(flags, "page-size") ?? structured.pageSize,
    cursor: stringFlag(flags, "cursor") ?? structured.cursor,
    budget: structured.budget
  });
}

function reflectionCallArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    typeName: stringFlag(flags, "type-name") ?? structured.typeName,
    methodName: stringFlag(flags, "method-name") ?? structured.methodName,
    args: structured.args,
    parameterTypes: structured.parameterTypes,
    instanceRef: structured.instanceRef,
    completionProbe: structured.completionProbe
  });
}

function typeSchemaArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    typeName: stringFlag(flags, "type-name") ?? structured.typeName,
    maxDepth: numberFlag(flags, "max-depth") ?? structured.maxDepth,
    maxMembers: numberFlag(flags, "max-members") ?? structured.maxMembers,
    maxDefinitions: numberFlag(flags, "max-definitions") ?? structured.maxDefinitions,
    pageSize: numberFlag(flags, "page-size") ?? structured.pageSize,
    cursor: stringFlag(flags, "cursor") ?? structured.cursor,
    budget: structured.budget
  });
}

export const reflectionBridgeSpecs: BridgeCommandSpec[] = [
  { group: "reflection", command: "method-find", bridgeCommand: "project-tool.run", toolName: "reflection.method.find", buildArgs: reflectionFindArgs },
  { group: "reflection", command: "method-call", bridgeCommand: "project-tool.run", toolName: "reflection.method.call", buildArgs: reflectionCallArgs, destructiveHint: true, supportsDryRun: true },
  { group: "type", command: "schema", bridgeCommand: "project-tool.run", toolName: "type.schema", buildArgs: typeSchemaArgs }
];
