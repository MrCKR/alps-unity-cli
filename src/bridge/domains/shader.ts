import { boolFlag, compactArgs, contentFlag, numberFlag, stringFlag } from "../args.js";
import type { BridgeCommandSpec, BridgeFlags } from "../types.js";

export const shaderBridgeSpecs: BridgeCommandSpec[] = [
  { group: "shader", command: "info", bridgeCommand: "project-tool.run", toolName: "shader.info", buildArgs: shaderArgs },
  { group: "shader", command: "read", bridgeCommand: "project-tool.run", toolName: "shader.read", buildArgs: shaderArgs },
  { group: "shader", command: "create", bridgeCommand: "project-tool.run", toolName: "shader.create", buildArgs: shaderArgs, destructiveHint: true },
  { group: "shader", command: "update", bridgeCommand: "project-tool.run", toolName: "shader.update", buildArgs: shaderArgs, destructiveHint: true },
  { group: "shader", command: "delete", bridgeCommand: "project-tool.run", toolName: "shader.delete", buildArgs: shaderArgs, destructiveHint: true, supportsDryRun: true },
  { group: "shader", command: "list", bridgeCommand: "project-tool.run", toolName: "shader.list", buildArgs: shaderListArgs },
  { group: "shader", command: "get-data", bridgeCommand: "project-tool.run", toolName: "shader.get-data", buildArgs: shaderArgs }
];

// 读取 tools run --input 中无法映射为简单 flag 的结构化字段。
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

function numberField(flags: BridgeFlags, key: string): number | undefined {
  const value = structuredInput(flags)[key];
  return typeof value === "number" ? value : undefined;
}

// 解析 shader 源码内容：支持 --content、--content-file 与结构化 content。
async function resolveShaderContent(flags: BridgeFlags): Promise<string | undefined> {
  const flagged = await contentFlag(flags);
  if (flagged !== undefined) return flagged;
  return stringField(flags, "content");
}

async function shaderArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Promise<Record<string, unknown>> {
  const structured = structuredInput(flags);
  return compactArgs({
    path: positionalTarget ?? stringFlag(flags, "path") ?? stringFlag(flags, "shader") ?? stringField(flags, "path") ?? stringField(flags, "assetPath"),
    assetPath: stringFlag(flags, "asset-path") ?? stringField(flags, "assetPath"),
    target: positionalTarget ?? stringFlag(flags, "target") ?? stringFlag(flags, "path") ?? stringFlag(flags, "shader") ?? stringField(flags, "path") ?? stringField(flags, "assetPath"),
    name: stringFlag(flags, "name") ?? stringField(flags, "name"),
    content: await resolveShaderContent(flags),
    overwrite: boolFlag(flags, "overwrite") ?? structured.overwrite,
    dryRun: boolFlag(flags, "dry-run") ?? structured.dryRun,
    includeMessages: boolFlag(flags, "include-messages") ?? boolField(flags, "includeMessages"),
    includeProperties: boolFlag(flags, "include-properties") ?? boolField(flags, "includeProperties"),
    includeSubshaders: boolFlag(flags, "include-subshaders") ?? boolField(flags, "includeSubshaders"),
    includeSourceCode: boolFlag(flags, "include-source-code") ?? boolField(flags, "includeSourceCode"),
    maxSubshaders: numberFlag(flags, "max-subshaders") ?? numberField(flags, "maxSubshaders"),
    maxPasses: numberFlag(flags, "max-passes") ?? numberField(flags, "maxPasses"),
    offset: numberFlag(flags, "offset") ?? numberField(flags, "offset"),
    maxBytes: numberFlag(flags, "max-bytes") ?? numberField(flags, "maxBytes")
  });
}

function shaderListArgs({ flags, positionalTarget }: Parameters<BridgeCommandSpec["buildArgs"]>[0]): Record<string, unknown> {
  return compactArgs({ keyword: positionalTarget ?? stringFlag(flags, "keyword") ?? stringFlag(flags, "search"), pageSize: numberFlag(flags, "page-size") ?? structuredInput(flags).pageSize });
}
