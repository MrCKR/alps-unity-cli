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

function methodFindArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
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

function methodInvokeArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
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

export const runtimeReflectionBridgeSpecs: BridgeCommandSpec[] = [
  { group: "runtime-reflection", command: "method.find", bridgeCommand: "catalog.editor.run", toolName: "runtime-reflection.method.find", buildArgs: methodFindArgs },
  { group: "runtime-reflection", command: "method.invoke", bridgeCommand: "catalog.editor.run", toolName: "runtime-reflection.method.invoke", buildArgs: methodInvokeArgs, destructiveHint: true, supportsDryRun: true },
  { group: "runtime-reflection", command: "type.schema", bridgeCommand: "catalog.editor.run", toolName: "runtime-reflection.type.schema", buildArgs: typeSchemaArgs }
];
