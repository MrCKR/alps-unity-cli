import { checkToolExtensionAvailability, extensionAvailabilityFlags } from "../../extension-availability.js";
import { publicToolDescriptor, resolveCatalogTool } from "../catalog.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface ToolsDescribeRequest { name: string }
export interface ToolsDescribeResult {
  name: string; description: string; inputSchema: Record<string, AnyJson>;
  destructiveHint: boolean; sideEffects?: string[]; playModeSupport: string; supportsDryRun: boolean;
  examples?: Array<{ description: string; input: Record<string, AnyJson>; execution?: { dryRun?: boolean; timeoutMs?: number } }>;
  target: string; available?: false; unavailableReason?: string; requiresPackages?: string[];
}

export default defineTool<ToolsDescribeRequest, ToolsDescribeResult>({
  name: "tools.describe",
  summary: "Show one tool's input schema, examples, side effects, target, and availability.",
  description: "Return one compact invocation descriptor from the connected Unity instance catalog: input schema, structured examples, effects, target, and current unavailability when applicable. Internal routing and result schemas stay private.",
  group: "tools",
  handler: "offline",
  supportStatus: "cli-only",
  destructiveHint: false,
  sideEffects: [],
  playModeSupport: "supported",
  examples: [{ description: "Describe a tool's invocation contract", argv: ["tools", "run", "tools.describe", "--input", "{\"name\":\"asset.search\"}"] }],
  async execute(request, context) {
    if (!request.name) throw new StructuredToolError("TOOLS_DESCRIBE_NAME_REQUIRED", "Pass a tool name: unity_cli tools describe <tool>.", ["Run unity_cli tools group to see tool groups."]);
    const flags = context.flags ?? {};
    const resolved = await resolveCatalogTool(request.name, flags, context, `${context.requestId}_describe_execution`);
    if (resolved.ok === false) throw StructuredToolError.from(resolved.error ?? { code: "TOOL_DISCOVERY_FAILED", message: "Tool discovery failed." });
    if (!resolved.tool) throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found for target: ${request.name}.`, ["Run unity_cli tools group to see tool groups.", "Run unity_cli tools search <k1|k2|...> to find related tools."]);
    const currentTool = resolved.tool;
    const data = publicToolDescriptor(currentTool) as unknown as ToolsDescribeResult;
    const availability = await checkToolExtensionAvailability(currentTool, await extensionAvailabilityFlags(flags, context));
    if (!availability.available) {
      data.available = false;
      data.unavailableReason = `${availability.errorCode}: ${availability.message}`;
    }
    return data;
  }
});
