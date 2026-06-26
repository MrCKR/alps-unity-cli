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
    includeInstance: boolFlag(flags, "include-instance") ?? structured.includeInstance
  });
}

function reflectionCallArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    typeName: stringFlag(flags, "type-name") ?? structured.typeName,
    methodName: stringFlag(flags, "method-name") ?? structured.methodName,
    args: structured.args,
    instanceRef: structured.instanceRef,
    security: structured.security
  });
}

function typeSchemaArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    typeName: stringFlag(flags, "type-name") ?? structured.typeName,
    maxDepth: numberFlag(flags, "max-depth") ?? structured.maxDepth
  });
}

export const reflectionBridgeSpecs: BridgeCommandSpec[] = [
  { group: "reflection", command: "method-find", bridgeCommand: "reflection.method.find", buildArgs: reflectionFindArgs },
  { group: "reflection", command: "method-call", bridgeCommand: "reflection.method.call", buildArgs: reflectionCallArgs, supportsDryRun: true },
  { group: "type", command: "schema", bridgeCommand: "type.schema", buildArgs: typeSchemaArgs }
];
