import { boolFlag, compactArgs, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const packageBridgeSpecs: BridgeCommandSpec[] = [
  { group: "package", command: "embed", bridgeCommand: "project-tool.run", toolName: "package.embed", buildArgs: packageEmbedArgs, destructiveHint: true, supportsDryRun: true }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段（package/packageId）
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

function packageEmbedArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({
    package: positionalTarget ?? stringFlag(flags, "package") ?? stringFlag(flags, "package-id") ?? stringFlag(flags, "id") ?? stringField(flags, "package") ?? stringField(flags, "packageId"),
    dryRun: boolFlag(flags, "dry-run") ?? boolField(flags, "dryRun")
  });
}
