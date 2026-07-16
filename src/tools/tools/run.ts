import { executeBridgeCommand, type BridgeSecurityDescriptor } from "../../bridge/index.js";
import { confirmDestructiveExecution } from "../../security.js";
import type { AnyJson } from "../../schema/any-json.js";
import { catalogFlags, resolveCatalogTool } from "../catalog.js";
import { defineTool, findDefineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface ToolsRunRequest {
  name: string; input?: AnyJson; fingerprintMode?: string; positionalTarget?: string;
}
export interface ToolsRunResult { command?: string }

function targetInput(value: unknown): Record<string, unknown> {
  if (value === undefined) return {};
  if (value && typeof value === "object" && !Array.isArray(value)) return value as Record<string, unknown>;
  if (typeof value === "string") {
    try {
      const parsed = JSON.parse(value);
      if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) return parsed as Record<string, unknown>;
    } catch {
      throw new StructuredToolError("E_TOOL_INPUT_JSON_INVALID", "tools.run input must be a JSON object.");
    }
  }
  throw new StructuredToolError("E_SCHEMA_INVALID", "tools.run input must be an object.");
}

export default defineTool<ToolsRunRequest, ToolsRunResult>({
  name: "tools.run",
  summary: "Execute another tool by dotted name through the unified execution service.",
  description: "Execute a catalog tool without re-entering the CLI dispatcher. Routes runtime targets through runtime-tool.run; editor and both targets use project-tool.run unless --runtime is selected.",
  group: "tools",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "hybrid",
  supportStatus: "live-bridge",
  target: "both",
  examples: [{ description: "Run an editor-domain tool", argv: ["tools", "run", "tools.run", "--input", "{\"name\":\"scene.hierarchy\"}"] }],
  async execute(request, context) {
    if (!request.name) throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", "Tool name is required for tools.run.", ["Run unity_cli tools group to see tool groups."]);
    if (request.name === "tools.run") throw new StructuredToolError("E_TOOL_RECURSION_UNSUPPORTED", "tools.run cannot execute itself recursively.");

    const flags = catalogFlags(request as unknown as Record<string, unknown>, context.flags);
    const resolved = await resolveCatalogTool(request.name, flags, context, context.requestId);
    if (!resolved.ok) throw StructuredToolError.from(resolved.error);
    if (!resolved.tool) throw new StructuredToolError("E_TOOL_NOT_FOUND_FOR_TARGET", `Tool not found: ${request.name}.`);

    const confirmation = await confirmDestructiveExecution(flags, {
      command: resolved.tool.name,
      summary: resolved.tool.summary,
      destructiveHint: resolved.tool.destructiveHint,
      supportsDryRun: resolved.tool.supportsDryRun,
      requestId: context.requestId
    });
    if (confirmation && !confirmation.ok) throw StructuredToolError.from(confirmation.error);

    const rawRequest = request as unknown as Record<string, unknown>;
    const input = rawRequest.input !== undefined
      ? targetInput(rawRequest.input)
      : Object.fromEntries(Object.entries(rawRequest).filter(([key]) => !["name", "fingerprintMode", "positionalTarget"].includes(key)));
    const local = findDefineTool(request.name);
    if (local) return await local.execute(input, context) as ToolsRunResult;

    const runtimeTarget = resolved.tool.target === "runtime" || (resolved.tool.target === "both" && flags.runtime === true);
    const wrapper = runtimeTarget ? "runtime-tool.run" : "project-tool.run";
    const security: BridgeSecurityDescriptor = {
      name: resolved.tool.name,
      destructiveHint: resolved.tool.destructiveHint,
      supportsDryRun: resolved.tool.supportsDryRun === true,
      bridgeCommand: wrapper,
      liveDescriptorFingerprint: resolved.tool.liveDescriptorFingerprint
    };
    const bridgeFlags: Record<string, string | boolean> = { ...flags, input: JSON.stringify(input), __structuredInput: JSON.stringify(input) };
    delete bridgeFlags["input-file"];
    delete bridgeFlags.stdin;
    const result = await executeBridgeCommand(context.requestId, wrapper, bridgeFlags, request.name, context, security);
    if (!result.ok) throw StructuredToolError.from(result.error ?? { code: "BRIDGE_COMMAND_FAILED", message: "Bridge command failed." });
    return (result.data ?? {}) as ToolsRunResult;
  }
});
