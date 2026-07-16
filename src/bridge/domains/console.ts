import { boolFlag, compactArgs, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec } from "../types.js";

export const consoleBridgeSpecs: BridgeCommandSpec[] = [
  { group: "console", command: "get", bridgeCommand: "project-tool.run", toolName: "console.get", buildArgs: consoleGetArgs },
  { group: "console", command: "clear", bridgeCommand: "project-tool.run", toolName: "console.clear", buildArgs: ({ flags }) => compactArgs({ dryRun: boolFlag(flags, "dry-run") }), supportsDryRun: true }
];

function consoleGetArgs({ flags }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  const structured = structuredInput(flags);
  return compactArgs({
    type: stringFlag(flags, "type") ?? stringFlag(flags, "level") ?? stringValue(structured.type),
    filter: stringFlag(flags, "filter") ?? stringFlag(flags, "search") ?? stringValue(structured.filter) ?? stringValue(structured.search),
    count: numberFlag(flags, "count") ?? numberValue(structured.count),
    cursor: numberFlag(flags, "cursor") ?? numberValue(structured.cursor)
  });
}

/** 读取 tools run --input 中的结构化字段，保证 describe 示例可直接生效。 */
function structuredInput(flags: Record<string, string | boolean>): Record<string, unknown> {
  const raw = stringFlag(flags, "__structuredInput");
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
  } catch {
    return {};
  }
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}
