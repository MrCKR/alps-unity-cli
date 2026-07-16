import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { catalogFlags, publicToolDescriptor, resolveCatalogTool } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { ToolExample } from "../metadata/index.js";

export interface ToolsDescribeRequest { name: string; includeCustom?: boolean; project?: string }
export interface ToolsDescribeSchemaObject {}
export interface ToolsDescribeResult {
  command: string; name: string; summary: string; description: string; group: string; inputSchema: ToolsDescribeSchemaObject; outputSchema?: ToolsDescribeSchemaObject;
  destructiveHint: boolean; sideEffects?: string[]; playModeSupport?: string; supportsDryRun?: boolean; permissions?: string[]; examples?: ToolExample[]; source?: string;
  riskLevel: string; longRunning?: boolean; cancellation?: string; cliCommand: string; target: string; availabilityScope: string; supportStatus: string; requiresPackages?: string[];
  extensionAvailability?: string; extensionAvailabilityError?: string; extensionId?: string; missingPackageIds?: string[]; nextActions?: string[];
}

export default defineTool<ToolsDescribeRequest, ToolsDescribeResult>({
  name: "tools.describe",
  summary: "Show full metadata for a specific tool: input schema, output schema, support status, risks, and examples.",
  description: "Describe a tool from the shared TS-local and live Bridge catalog without dropping live security metadata.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "Describe a tool's full schema", argv: ["tools", "run", "tools.describe", "--input", "{\"name\":\"asset.search\"}"] }],
  async execute(request, context) {
    if (!request.name) throw new StructuredToolError("TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unity_cli tools describe <tool>.", ["Run unity_cli tools group to see tool groups."]);
    const flags = catalogFlags(request as unknown as Record<string, unknown>, context.flags);
    const resolved = await resolveCatalogTool(request.name, flags, context, context.requestId);
    if (resolved.ok === false) throw StructuredToolError.from(resolved.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    if (!resolved.tool) throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${request.name}.`, ["Run unity_cli tools group to see tool groups.", "Run unity_cli tools search <keyword> to find related tools."]);
    const data = { command: "tools.describe", ...publicToolDescriptor(resolved.tool) } as unknown as ToolsDescribeResult;
    const availability = await checkToolExtensionAvailability(resolved.tool, await extensionAvailabilityFlags(flags, context));
    if (!availability.available) {
      data.extensionAvailability = "unavailable";
      data.extensionAvailabilityError = availability.errorCode;
      data.extensionId = availability.extensionId;
      data.missingPackageIds = availability.availabilityResult?.missingPackageIds ?? resolved.tool.requiresPackages ?? [];
      data.nextActions = availability.nextActions;
    } else if (availability.extensionId) {
      data.extensionAvailability = availability.availabilityResult?.status ?? "available";
      data.extensionId = availability.extensionId;
    }
    return data;
  }
});
