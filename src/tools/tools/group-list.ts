import { discoverProjectTools, shouldDiscoverProjectTools } from "../../project-tool-discovery.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import {
  builtinToolMetadata,
  extensionToolMetadata,
  supportStatusForTool,
  toolCliCommand,
  toolSource,
  type ToolMetadata
} from "../metadata/index.js";

export interface ToolsGroupListRequest {
  group: string;
  includeCustom?: boolean;
  bridgeEndpoint?: string;
  target?: string;
  instance?: string;
  runtime?: boolean;
}

export interface ToolsGroupListItem {
  name: string;
  summary: string;
  group: string;
  source: string;
  destructiveHint: boolean;
  playModeSupport?: string;
  supportStatus: string;
  target: string;
  requiresPackages?: string[];
  cliCommand: string;
}

export interface ToolsGroupListResult {
  command: string;
  group: string;
  count: number;
  tools: ToolsGroupListItem[];
  nextActions: string[];
}

type DiscoveryFlags = Record<string, string | boolean>;

// 构造 project custom tool discovery 需要的公开 CLI flags 形态。
// 直接子命令使用 context.flags；通过 tools.run 包装调用时使用 request 字段。
function buildDiscoveryFlags(request: ToolsGroupListRequest, baseFlags: DiscoveryFlags = {}): DiscoveryFlags {
  const flags: DiscoveryFlags = { ...baseFlags };
  if (request.includeCustom !== undefined) flags["include-custom"] = request.includeCustom;
  if (request.bridgeEndpoint !== undefined) flags["bridge-endpoint"] = request.bridgeEndpoint;
  if (request.target !== undefined) flags.target = request.target;
  if (request.instance !== undefined) flags.instance = request.instance;
  if (request.runtime !== undefined) flags.runtime = request.runtime;
  return flags;
}

// 按工具名去重，custom tool 排在后面时覆盖同名静态工具。
function uniqueToolsByName(tools: ToolMetadata[]): ToolMetadata[] {
  const map = new Map<string, ToolMetadata>();
  for (const tool of tools) {
    map.set(tool.name, tool);
  }
  return Array.from(map.values());
}

// 将 project custom discovery 失败转成 defineTool 可识别的结构化错误。
function throwDiscoveryError(result: { error?: { code: string; message: string; nextActions?: string[]; transient?: boolean; retryAfterMs?: number } }): never {
  const error = result.error ?? {
    code: "PROJECT_TOOL_DISCOVERY_FAILED",
    message: "Project custom tool discovery failed.",
    nextActions: ["Retry without includeCustom or check the Unity bridge target."]
  };
  throw new StructuredToolError(
    error.code,
    error.message,
    error.nextActions ?? [],
    { transient: error.transient, retryAfterMs: error.retryAfterMs }
  );
}

// 转换为 group list 的稳定短摘要，避免展开完整 schema。
function toGroupListItem(tool: ToolMetadata, customTools: ToolMetadata[]): ToolsGroupListItem {
  return {
    name: tool.name,
    summary: tool.summary,
    group: tool.group,
    source: toolSource(tool, customTools.includes(tool) ? "project" : "built-in"),
    destructiveHint: tool.destructiveHint,
    playModeSupport: tool.playModeSupport,
    supportStatus: supportStatusForTool(tool),
    target: tool.target ?? "editor",
    ...(tool.requiresPackages ? { requiresPackages: tool.requiresPackages } : {}),
    cliCommand: toolCliCommand(tool)
  };
}

export default defineTool<ToolsGroupListRequest, ToolsGroupListResult>({
  name: "tools.group-list",
  summary: "List tools in a specific group with short routing summaries and support status.",
  description: "List tools in a specific group with short routing summaries, destructiveHint, source, target, playModeSupport, and supportStatus. Does NOT expand full schema; use tools describe <tool> for the complete input/output schema. Pass includeCustom with a target, instance, or bridgeEndpoint to include project custom IUnityCliTool tools.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [
    { description: "List tools in the camera group", argv: ["tools", "group", "list", "camera"] }
  ],
  async execute(request, context) {
    const groupName = request.group;
    if (!groupName) {
      throw new StructuredToolError("TOOLS_GROUP_LIST_NAME_REQUIRED", "Pass a group name: unity_cli tools group list <group>.", [
        "Run unity_cli tools group to see all tool groups."
      ]);
    }

    const flags = buildDiscoveryFlags(request, context.flags);
    const builtinTools = builtinToolMetadata().filter((tool) => tool.group === groupName);
    const extensionTools = extensionToolMetadata().filter((tool) => tool.group === groupName);
    let customTools: ToolMetadata[] = [];

    if (shouldDiscoverProjectTools(flags, context)) {
      const discovery = await discoverProjectTools(flags, {
        projectTools: context.projectTools,
        bridgeExecute: context.bridgeExecute,
        bridgeEndpoint: request.bridgeEndpoint ?? context.bridgeEndpoint
      }, context.requestId, { requireBridge: request.includeCustom === true });
      if (discovery.ok === false) {
        throwDiscoveryError(discovery);
      }
      customTools = discovery.tools.filter((tool: ToolMetadata) => tool.group === groupName);
    }

    const tools = uniqueToolsByName([...builtinTools, ...extensionTools, ...customTools]);
    if (tools.length === 0) {
      throw new StructuredToolError("E_TOOL_GROUP_NOT_FOUND", `Tool group not found: ${groupName}.`, [
        "Run unity_cli tools group to see all tool groups."
      ]);
    }

    return {
      command: "tools.group.list",
      group: groupName,
      count: tools.length,
      tools: tools.map((tool) => toGroupListItem(tool, customTools)),
      nextActions: [
        "Run unity_cli tools describe <tool> for full metadata including schema.",
        "Run unity_cli tools run <tool> to execute a tool."
      ]
    };
  }
});
