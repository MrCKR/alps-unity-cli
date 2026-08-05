import { discoverProjectTools } from "../../project-tool-discovery.js";
import type { AnyJson } from "../../schema/any-json.js";
import { publicToolDescriptor } from "../../tools/catalog.js";
import { contextErrorFromResult, defineContext } from "../define.js";

export interface ToolsCustomRequest {}

export interface ToolsCustomMetadata {
  name: string;
  summary: string;
  description: string;
  group: string;
  inputSchema: Record<string, AnyJson>;
  outputSchema: Record<string, AnyJson>;
  destructiveHint: boolean;
  sideEffects?: string[];
  playModeSupport: string;
  supportsDryRun: boolean;
  permissions?: string[];
  examples?: Array<{ description: string; argv: string[] }>;
  source?: string;
  target: string;
  availabilityScope: string;
  available: boolean;
  unavailableReason?: string;
  requiresPackages?: string[];
}

export interface ToolsCustomResult {
  tools: ToolsCustomMetadata[];
  count: number;
}

export default defineContext<ToolsCustomRequest, ToolsCustomResult>({
  name: "tools.custom",
  description: "Read custom Unity project tools discovered through IUnityCliTool descriptors.",
  category: "tools",
  source: "built-in",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  examples: [{ description: "Discover project-defined tools through a live Bridge", argv: ["context", "read", "tools.custom", "--input", "{}"], input: {} }],
  async read(_request, ctx) {
    const discovery = await discoverProjectTools(ctx.flags ?? {}, ctx, ctx.requestId, { requireBridge: true });
    if (!discovery.ok) {
      throw contextErrorFromResult(discovery);
    }
    const tools = discovery.tools.filter((tool) => tool.source === "project").map((tool) => publicToolDescriptor(tool) as unknown as ToolsCustomMetadata);
    return { tools, count: tools.length };
  }
});
