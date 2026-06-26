import { boolFlag, compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const scriptableObjectBridgeSpecs: BridgeCommandSpec[] = [
  { group: "scriptable-object", command: "create", bridgeCommand: "scriptable-object.create", buildArgs: scriptableObjectArgs },
  { group: "scriptable-object", command: "info", bridgeCommand: "scriptable-object.info", buildArgs: scriptableObjectArgs },
  { group: "scriptable-object", command: "modify", bridgeCommand: "scriptable-object.modify", buildArgs: scriptableObjectArgs }
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

function stringField(flags: BridgeFlags, key: string): string | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "string" ? value : undefined;
}

function boolField(flags: BridgeFlags, key: string): boolean | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "boolean" ? value : undefined;
}

function scriptableObjectArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"),
    target: stringFlag(flags, "target") ?? stringField(flags, "target") ?? stringField(flags, "path"),
    type: stringFlag(flags, "type") ?? stringField(flags, "type"),
    property: stringFlag(flags, "property") ?? stringField(flags, "property"),
    value: stringFlag(flags, "value") ?? stringField(flags, "value"),
    overwrite: boolFlag(flags, "overwrite") ?? boolField(flags, "overwrite")
  });
}
