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

function codeArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    sourcePath: stringFlag(flags, "file"),
    sourceText: stringFlag(flags, "code"),
    sourceKind: stringFlag(flags, "file") ? "file" : stringFlag(flags, "code") ? "inline" : boolFlag(flags, "stdin") ? "stdin" : undefined,
    timeoutMs: numberFlag(flags, "timeout-ms"),
    artifactDir: stringFlag(flags, "artifact-dir"),
    argsJson: stringFlag(flags, "args"),
    refs: stringFlag(flags, "refs"),
    defines: stringFlag(flags, "define")
  });
}

function consoleReadArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    type: stringFlag(flags, "type") ?? stringFlag(flags, "level") ?? stringValue(structured.type),
    filter: stringFlag(flags, "filter") ?? stringFlag(flags, "search") ?? stringValue(structured.filter) ?? stringValue(structured.search),
    count: numberFlag(flags, "count") ?? numberValue(structured.count),
    cursor: stringFlag(flags, "cursor") ?? stringValue(structured.cursor),
    budget: structured.budget
  });
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

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/**
 * debug 域 spec：本地 typed code 工具保留内部 transport 名，其余 13 项经通用 catalog transport 路由。
 */
export const debugBridgeSpecs: BridgeCommandSpec[] = [
  { group: "debug", command: "code.execute", bridgeCommand: "code.execute", toolName: "debug.code.execute", buildArgs: codeArgs, destructiveHint: true, supportsDryRun: true },
  { group: "debug", command: "code.flow.execute", bridgeCommand: "code.flow.execute", toolName: "debug.code.flow.execute", buildArgs: structuredPassthrough, destructiveHint: true, supportsDryRun: true },
  { group: "debug", command: "code.flow.wait", bridgeCommand: "code.flow.wait", toolName: "debug.code.flow.wait", buildArgs: structuredPassthrough },
  { group: "debug", command: "code.flow.cancel", bridgeCommand: "code.flow.cancel", toolName: "debug.code.flow.cancel", buildArgs: structuredPassthrough, destructiveHint: true },
  { group: "debug", command: "console.read", bridgeCommand: "catalog.editor.run", toolName: "debug.console.read", buildArgs: consoleReadArgs },
  { group: "debug", command: "console.clear", bridgeCommand: "catalog.editor.run", toolName: "debug.console.clear", buildArgs: ({ flags }) => compactArgs({ dryRun: boolFlag(flags, "dry-run") }), supportsDryRun: true },
  { group: "debug", command: "method.find", bridgeCommand: "catalog.editor.run", toolName: "debug.method.find", buildArgs: methodFindArgs },
  { group: "debug", command: "method.invoke", bridgeCommand: "catalog.editor.run", toolName: "debug.method.invoke", buildArgs: methodInvokeArgs, destructiveHint: true, supportsDryRun: true },
  { group: "debug", command: "type.schema", bridgeCommand: "catalog.editor.run", toolName: "debug.type.schema", buildArgs: typeSchemaArgs },
  { group: "debug", command: "hook.install", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.install", buildArgs: structuredPassthrough },
  { group: "debug", command: "hook.replace", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.replace", buildArgs: structuredPassthrough },
  { group: "debug", command: "hook.enable", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.enable", buildArgs: structuredPassthrough },
  { group: "debug", command: "hook.disable", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.disable", buildArgs: structuredPassthrough },
  { group: "debug", command: "hook.remove", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.remove", buildArgs: structuredPassthrough },
  { group: "debug", command: "hook.list", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.list", buildArgs: structuredPassthrough },
  { group: "debug", command: "hook.info", bridgeCommand: "catalog.editor.run", toolName: "debug.hook.info", buildArgs: structuredPassthrough },
  { group: "debug", command: "events.read", bridgeCommand: "catalog.editor.run", toolName: "debug.events.read", buildArgs: structuredPassthrough }
];

/** hook/events 的 typed input 由 live schema 投影，spec 只搬运结构化字段。 */
function structuredPassthrough({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return structuredInput(flags);
}
