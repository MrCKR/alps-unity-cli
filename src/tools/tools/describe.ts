import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { publicToolDescriptor, resolveCatalogTool } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";
import type { ToolContractProfile } from "../metadata/index.js";

export interface ToolsDescribeRequest { name: string }
export interface ToolsDescribeResult {
  name: string; summary: string; description: string; group: string; inputSchema: Record<string, AnyJson>; outputSchema: Record<string, AnyJson>;
  destructiveHint: boolean; sideEffects?: string[]; playModeSupport: string; supportsDryRun: boolean; contractProfile: ToolContractProfile; permissions?: string[];
  examples?: Array<{ description: string; argv: string[] }>; source?: string; target: string; availabilityScope: string; available: boolean;
  unavailableReason?: string; requiresPackages?: string[]; extensionId?: string;
}

export default defineTool<ToolsDescribeRequest, ToolsDescribeResult>({
  name: "tools.describe",
  summary: "Show one tool's schemas, examples, side effects, target, and availability.",
  description: "Describe one Tool from the connected Unity instance catalog, including schemas, examples, effects, target, and current availability while keeping internal routing private.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "Describe a tool's full schema", argv: ["tools", "run", "tools.describe", "--input", "{\"name\":\"asset.search\"}"] }],
  async execute(request, context) {
    if (!request.name) throw new StructuredToolError("TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unity_cli tools describe <tool>.", ["Run unity_cli tools group to see tool groups."]);
    const flags = context.flags ?? {};
    const resolved = await resolveCatalogTool(request.name, flags, context, `${context.requestId}_describe_execution`);
    if (resolved.ok === false) throw StructuredToolError.from(resolved.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    if (!resolved.tool) throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${request.name}.`, ["Run unity_cli tools group to see tool groups.", "Run unity_cli tools search <keyword> to find related tools."]);
    const currentTool = resolved.tool;
    const data = publicToolDescriptor(currentTool) as unknown as ToolsDescribeResult;
    const availability = await checkToolExtensionAvailability(currentTool, await extensionAvailabilityFlags(flags, context));
    if (!availability.available) {
      data.available = false;
      data.unavailableReason = `${availability.errorCode}: ${availability.message}`;
    }
    if (availability.extensionId) data.extensionId = availability.extensionId;
    return data;
  }
});
