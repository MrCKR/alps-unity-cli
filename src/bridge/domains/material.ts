import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const materialBridgeSpecs: BridgeCommandSpec[] = [
  { group: "material", command: "info", bridgeCommand: "project-tool.run", toolName: "material.info", buildArgs: materialArgs },
  { group: "material", command: "create", bridgeCommand: "project-tool.run", toolName: "material.create", buildArgs: materialArgs, destructiveHint: true },
  { group: "material", command: "assign", bridgeCommand: "project-tool.run", toolName: "material.assign", buildArgs: materialArgs },
  { group: "material", command: "set", bridgeCommand: "project-tool.run", toolName: "material.set", buildArgs: materialArgs, destructiveHint: true },
  { group: "material", command: "set-renderer-color", bridgeCommand: "project-tool.run", toolName: "material.set-renderer-color", buildArgs: materialArgs, supportsDryRun: true }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（如 namePattern）
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

function valueString(value: unknown): string | undefined {
  return typeof value === "string" || typeof value === "number" || typeof value === "boolean" ? String(value) : undefined;
}

function materialArgs({ command, flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  if (command === "material.create") {
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? stringField(flags, "path"),
      shader: stringFlag(flags, "shader") ?? stringField(flags, "shader"),
      overwrite: boolFlag(flags, "overwrite") ?? structuredInput(flags).overwrite
    });
  }
  if (command === "material.assign") {
    const structured = structuredInput(flags);
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? structured.target,
      material: stringFlag(flags, "material") ?? stringFlag(flags, "path") ?? structured.material,
      slot: numberFlag(flags, "slot") ?? numberFlag(flags, "material-index") ?? structured.slot
    });
  }
  if (command === "material.set") {
    const structured = structuredInput(flags);
    return compactArgs({
      path: positionalTarget ?? stringFlag(flags, "path") ?? structured.path,
      property: stringFlag(flags, "property") ?? structured.property,
      value: stringFlag(flags, "value") ?? valueString(structured.value),
      type: stringFlag(flags, "type") ?? structured.type
    });
  }
  if (command === "material.set-renderer-color") {
    const structured = structuredInput(flags);
    return compactArgs({
      target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? structured.target,
      property: stringFlag(flags, "property") ?? structured.property,
      color: stringFlag(flags, "color") ?? stringFlag(flags, "value") ?? valueString(structured.color) ?? valueString(structured.value),
      slot: numberFlag(flags, "slot") ?? structured.slot,
      dryRun: boolFlag(flags, "dry-run") ?? structured.dryRun
    });
  }
  // material.info：支持 namePattern 结构化字段透传，用于 token 安全的轻量属性读取
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "material") ?? stringField(flags, "path"),
    namePattern: stringFlag(flags, "name-pattern") ?? stringField(flags, "namePattern")
  });
}
