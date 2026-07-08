import { discoverProjectTools, shouldDiscoverProjectTools } from "../../project-tool-discovery.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import {
  searchTools,
  supportStatusForTool,
  toolAvailabilityScope,
  toolCliCommand,
  toolSource,
  type ToolMetadata
} from "../metadata/index.js";

export interface ToolsSearchRequest {
  keyword: string;
  format?: "json" | "text";
  includeCustom?: boolean;
  bridgeEndpoint?: string;
  target?: string;
  instance?: string;
  runtime?: boolean;
}

export interface ToolsSearchItem {
  name: string;
  summary: string;
  group: string;
  source: string;
  cliCommand?: string;
  destructiveHint: boolean;
  playModeSupport?: string;
  supportStatus: string;
  target: string;
  availabilityScope?: string;
  requiresPackages?: string[];
}

export interface ToolsSearchResult {
  command: string;
  keyword: string;
  count: number;
  tools: ToolsSearchItem[];
  text?: string;
}

type DiscoveryFlags = Record<string, string | boolean>;

// 构造 project custom tool discovery 需要的公开 CLI flags 形态。
// 直接子命令使用 context.flags；通过 tools.run 包装调用时使用 request 字段。
function buildDiscoveryFlags(request: ToolsSearchRequest, baseFlags: DiscoveryFlags = {}): DiscoveryFlags {
  const flags: DiscoveryFlags = { ...baseFlags };
  if (request.includeCustom !== undefined) flags["include-custom"] = request.includeCustom;
  if (request.bridgeEndpoint !== undefined) flags["bridge-endpoint"] = request.bridgeEndpoint;
  if (request.target !== undefined) flags.target = request.target;
  if (request.instance !== undefined) flags.instance = request.instance;
  if (request.runtime !== undefined) flags.runtime = request.runtime;
  return flags;
}

// 搜索 project custom 工具，保持旧 tools.search 的字段匹配规则。
function searchCustomTools(keyword: string, tools: ToolMetadata[]): ToolMetadata[] {
  const lower = keyword.toLowerCase();
  return tools.filter((tool) =>
    tool.name.toLowerCase().includes(lower) ||
    tool.summary.toLowerCase().includes(lower) ||
    tool.description.toLowerCase().includes(lower) ||
    tool.group.toLowerCase().includes(lower)
  );
}

// 按工具名去重，project custom 工具排在后面时覆盖同名静态工具。
function uniqueToolsByName(tools: ToolMetadata[]): ToolMetadata[] {
  const map = new Map<string, ToolMetadata>();
  for (const tool of tools) {
    map.set(tool.name, tool);
  }
  return Array.from(map.values());
}

// 转换为 tools.search 对外稳定摘要，避免泄露内部 descriptor 全量字段。
function toSearchItem(tool: ToolMetadata): ToolsSearchItem {
  const cliCommand = toolCliCommand(tool);
  return {
    name: tool.name,
    summary: tool.summary,
    group: tool.group,
    source: toolSource(tool),
    destructiveHint: tool.destructiveHint,
    playModeSupport: tool.playModeSupport,
    supportStatus: supportStatusForTool(tool),
    ...(cliCommand ? { cliCommand } : {}),
    target: tool.target ?? "editor",
    availabilityScope: toolAvailabilityScope(tool),
    ...(tool.requiresPackages ? { requiresPackages: tool.requiresPackages } : {})
  };
}

// text 格式用于保留旧 CLI 交互输出，JSON 仍是默认机器接口。
function renderSearchText(keyword: string, tools: ToolsSearchItem[]): string {
  if (tools.length === 0) {
    return `No tools matched ${keyword}.`;
  }
  return tools
    .map((tool) => {
      const command = tool.cliCommand ? ` (${tool.cliCommand})` : "";
      return `${tool.name} [${tool.group}] ${tool.supportStatus}${command} - ${tool.summary}`;
    })
    .join("\n");
}

export default defineTool<ToolsSearchRequest, ToolsSearchResult>({
  name: "tools.search",
  summary: "Search tools by keyword across name, summary, description, and group.",
  description: "Search tools by keyword across name, summary, description, and group. Use this when you do not know the exact group; results are ranked. Then tools describe <tool> for full schema. Pass includeCustom with a target, instance, or bridgeEndpoint to include project custom tools from the active Unity project.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [
    { description: "Search for hierarchy tools", argv: ["tools", "search", "hierarchy"] },
    { description: "Search with text output", argv: ["tools", "run", "tools.search", "--input", "{\"keyword\":\"hierarchy\",\"format\":\"text\"}"] }
  ],
  async execute(request, context) {
    const flags = buildDiscoveryFlags(request, context.flags);
    let tools: ToolMetadata[] = searchTools(request.keyword);

    if (shouldDiscoverProjectTools(flags, context)) {
      const discovery = await discoverProjectTools(flags, {
        projectTools: context.projectTools,
        bridgeExecute: context.bridgeExecute,
        bridgeEndpoint: request.bridgeEndpoint ?? context.bridgeEndpoint
      }, context.requestId, { requireBridge: request.includeCustom === true });
      if (discovery.ok === false) {
        const error = discovery.error ?? {
          code: "PROJECT_TOOL_DISCOVERY_FAILED",
          message: "Project custom tool discovery failed.",
          nextActions: ["Retry without includeCustom or check the Unity bridge target."],
          transient: false,
          retryAfterMs: undefined
        };
        throw new StructuredToolError(
          error.code,
          error.message,
          error.nextActions,
          { transient: error.transient, retryAfterMs: error.retryAfterMs }
        );
      }
      tools = uniqueToolsByName([...tools, ...searchCustomTools(request.keyword, discovery.tools)]);
    }

    const items = tools.map(toSearchItem);
    return {
      command: "tools.search",
      keyword: request.keyword,
      count: items.length,
      tools: items,
      ...(request.format === "text" ? { text: renderSearchText(request.keyword, items) } : {})
    };
  }
});
