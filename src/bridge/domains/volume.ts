import { compactArgs, queryArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const volumeBridgeSpecs: BridgeCommandSpec[] = [
  { group: "volume", command: "list", bridgeCommand: "catalog.editor.run", toolName: "volume.list", buildArgs: volumeArgs }
];

function volumeArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const s = structuredInput(flags);
  return compactArgs({ ...queryArgs(flags), target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "name") ?? stringField(s, "target") });
}

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段
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

function stringField(s: Record<string, unknown>, key: string): string | undefined {
  const value = s[key];
  return typeof value === "string" ? value : undefined;
}
