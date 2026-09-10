import { boolFlag, compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const scriptableObjectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scriptable-object", command: "create", bridgeCommand: "catalog.editor.run", toolName: "scriptable-object.create", buildArgs: scriptableObjectArgs, destructiveHint: true, supportsDryRun: true },
  { group: "scriptable-object", command: "info", bridgeCommand: "catalog.editor.run", toolName: "scriptable-object.info", buildArgs: scriptableObjectArgs },
  { group: "scriptable-object", command: "list-types", bridgeCommand: "catalog.editor.run", toolName: "scriptable-object.list-types", buildArgs: scriptableObjectArgs },
  { group: "scriptable-object", command: "modify", bridgeCommand: "catalog.editor.run", toolName: "scriptable-object.modify", buildArgs: scriptableObjectArgs, destructiveHint: true, supportsDryRun: true }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（type/property/value 等）
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

function stringField(input: Record<string, unknown>, flags: BridgeFlags, key: string): string | undefined {
  const value = input[key];
  return stringFlag(flags, key) ?? (typeof value === "string" ? value : undefined);
}

function boolField(input: Record<string, unknown>, flags: BridgeFlags, key: string): boolean | undefined {
  const flag = boolFlag(flags, key);
  if (typeof flag === "boolean") return flag;
  const value = input[key];
  return typeof value === "boolean" ? value : undefined;
}

function inputField(input: Record<string, unknown>, key: string): unknown {
  return Object.prototype.hasOwnProperty.call(input, key) ? input[key] : undefined;
}

function scriptableObjectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const input = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringField(input, flags, "path"),
    target: stringField(input, flags, "target") ?? stringField(input, flags, "path"),
    type: stringField(input, flags, "type"),
    property: stringField(input, flags, "property"),
    value: stringFlag(flags, "value") ?? inputField(input, "value"),
    patches: inputField(input, "patches"),
    overwrite: boolField(input, flags, "overwrite"),
    namePattern: stringField(input, flags, "namePattern") ?? stringField(input, flags, "name-pattern") ?? stringField(input, flags, "query"),
    assemblyPattern: stringField(input, flags, "assemblyPattern") ?? stringField(input, flags, "assembly-pattern"),
    includeAbstract: boolField(input, flags, "includeAbstract"),
    pageSize: inputField(input, "pageSize"),
    cursor: inputField(input, "cursor"),
    dryRun: boolField(input, flags, "dryRun")
  });
}
