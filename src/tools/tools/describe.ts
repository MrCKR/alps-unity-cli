import { checkToolExtensionAvailability } from "../../extension-availability.js";
import { discoverProjectTools } from "../../project-tool-discovery.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import {
  findToolByName,
  inferToolRiskLevel,
  isLongRunningTool,
  supportStatusForTool,
  toolAvailabilityScope,
  toolCancellation,
  toolCliCommand,
  type ToolExample,
  type ToolMetadata
} from "../metadata/index.js";

export interface ToolsDescribeRequest {
  name: string;
  includeCustom?: boolean;
  bridgeEndpoint?: string;
  target?: string;
  instance?: string;
  runtime?: boolean;
  project?: string;
}

export interface ToolsDescribeSchemaObject {}

export interface ToolsDescribeResult {
  command: string;
  name: string;
  summary: string;
  description: string;
  group: string;
  inputSchema: ToolsDescribeSchemaObject;
  outputSchema?: ToolsDescribeSchemaObject;
  destructiveHint: boolean;
  sideEffects?: string[];
  playModeSupport?: string;
  supportsDryRun?: boolean;
  permissions?: string[];
  examples?: ToolExample[];
  source?: string;
  riskLevel: string;
  longRunning?: boolean;
  cancellation?: string;
  cliCommand: string;
  target: string;
  availabilityScope: string;
  supportStatus: string;
  requiresPackages?: string[];
  extensionAvailability?: string;
  extensionId?: string;
  missingPackageIds?: string[];
  nextActions?: string[];
}

type DiscoveryFlags = Record<string, string | boolean>;
type DescribableTool = ToolMetadata & { source?: string };

// 构造 project custom tool discovery 与 extension availability 共用的 CLI flags 形态。
// 直接 `tools describe` 会把 --instance/--target 放在 context.flags；`tools run tools.describe`
// 会把它们放在 request 中。这里合并两者，request 字段优先。
function buildDescribeFlags(request: ToolsDescribeRequest, baseFlags: DiscoveryFlags = {}): DiscoveryFlags {
  const flags: DiscoveryFlags = { ...baseFlags };
  if (request.includeCustom !== undefined) flags["include-custom"] = request.includeCustom;
  if (request.bridgeEndpoint !== undefined) flags["bridge-endpoint"] = request.bridgeEndpoint;
  if (request.target !== undefined) flags.target = request.target;
  if (request.instance !== undefined) flags.instance = request.instance;
  if (request.runtime !== undefined) flags.runtime = request.runtime;
  if (request.project !== undefined) flags.project = request.project;
  return flags;
}

// 查找 project custom descriptor；发现失败保持旧 tools.describe 的 not-found 行为。
async function findProjectToolByName(name: string, flags: DiscoveryFlags, context: { projectTools?: ToolMetadata[]; bridgeExecute?: unknown; bridgeEndpoint?: string; requestId: string }): Promise<ToolMetadata | undefined> {
  const discovery = await discoverProjectTools(flags, {
    projectTools: context.projectTools,
    bridgeExecute: context.bridgeExecute as never,
    bridgeEndpoint: context.bridgeEndpoint
  }, context.requestId);
  if (discovery.ok === false) return undefined;
  return discovery.tools.find((tool: ToolMetadata) => tool.name === name);
}

// 生成 tools.describe 的稳定公开 descriptor，避免泄露 handler 等内部路由实现字段。
function toPublicDescriptor(tool: DescribableTool): ToolsDescribeResult {
  const {
    handler: _handler,
    timeoutMs: _timeoutMs,
    available: _available,
    ...publicTool
  } = tool;
  const longRunning = isLongRunningTool(tool);
  const cancellation = toolCancellation(tool);
  return {
    command: "tools.describe",
    ...publicTool,
    riskLevel: inferToolRiskLevel(tool),
    ...(longRunning ? { longRunning } : {}),
    ...(cancellation !== "none" ? { cancellation } : {}),
    cliCommand: toolCliCommand(tool),
    target: tool.target ?? "editor",
    availabilityScope: toolAvailabilityScope(tool),
    supportStatus: supportStatusForTool(tool)
  } as ToolsDescribeResult;
}

export default defineTool<ToolsDescribeRequest, ToolsDescribeResult>({
  name: "tools.describe",
  summary: "Show full metadata for a specific tool: input schema, output schema, support status, risks, and examples.",
  description: "Show full metadata for a specific tool: input schema, output schema, support status, playModeSupport, sideEffects, riskLevel, and examples. This is the primary entry before tools run — read it once to construct a correct one-shot call. Unknown names return E_TOOL_NOT_FOUND_FOR_TARGET. Pass includeCustom with a target, instance, or bridgeEndpoint to describe project custom tools. Pass project when you need extension package availability for a Unity project.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [
    { description: "Describe a tool's full schema", argv: ["tools", "describe", "asset.search"] },
    { description: "Describe a project custom tool", argv: ["tools", "run", "tools.describe", "--input", "{\"name\":\"sample.echo\",\"includeCustom\":true,\"bridgeEndpoint\":\"http://127.0.0.1:47395\"}"] }
  ],
  async execute(request, context) {
    if (!request.name) {
      throw new StructuredToolError("TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unity_cli tools describe <tool>.", [
        "Run unity_cli tools group to see tool groups."
      ]);
    }

    const flags = buildDescribeFlags(request, context.flags);
    const builtinTool = findToolByName(request.name);
    const projectTool = builtinTool ? undefined : await findProjectToolByName(request.name, flags, {
      projectTools: context.projectTools,
      bridgeExecute: context.bridgeExecute,
      bridgeEndpoint: request.bridgeEndpoint ?? context.bridgeEndpoint,
      requestId: context.requestId
    });
    const tool = builtinTool ?? projectTool;

    if (!tool) {
      throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${request.name}.`, [
        "Run unity_cli tools group to see tool groups.",
        "Run unity_cli tools group list <group> to see tools in a group.",
        "Run unity_cli tools group to see all tool groups.",
        "Run unity_cli context read tools.custom to inspect Unity project custom tools and their groups.",
        "Run unity_cli tools group list <group> --include-custom for the custom tool's actual group.",
        "Run unity_cli tools search <keyword> --include-custom to find related custom tools."
      ]);
    }

    const extensionCheck = await checkToolExtensionAvailability(tool, flags);
    const data = toPublicDescriptor(tool);
    if (!extensionCheck.available) {
      data.extensionAvailability = "unavailable";
      data.extensionId = extensionCheck.extensionId;
      data.missingPackageIds = extensionCheck.availabilityResult?.missingPackageIds ?? tool.requiresPackages ?? [];
      data.nextActions = extensionCheck.nextActions;
    } else if (extensionCheck.extensionId) {
      data.extensionAvailability = extensionCheck.availabilityResult?.status ?? "available";
      data.extensionId = extensionCheck.extensionId;
    }
    return data;
  }
});
