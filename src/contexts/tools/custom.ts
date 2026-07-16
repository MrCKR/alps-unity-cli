import { discoverProjectTools } from "../../project-tool-discovery.js";
import type { ToolMetadata } from "../../tools/metadata/index.js";
import type { AnyJson } from "../../schema/any-json.js";
import { contextErrorFromResult, defineContext } from "../define.js";

export interface ToolsCustomRequest {}

export interface ToolsCustomMetadata extends Omit<ToolMetadata, "inputSchema" | "outputSchema"> {
  inputSchema: Record<string, AnyJson>;
  outputSchema?: Record<string, AnyJson>;
}

export interface ToolsCustomResult {
  tools: ToolsCustomMetadata[];
  count: number;
  mode: string;
}

export default defineContext<ToolsCustomRequest, ToolsCustomResult>({
  name: "tools.custom",
  description: "Read custom Unity project tools discovered through IUnityCliTool descriptors.",
  category: "tools",
  source: "built-in",
  target: "editor",
  availabilityScope: "editorSession",
  playModeSupport: "supported",
  supportStatus: "live-bridge",
  refreshBehavior: "on-demand",
  examples: [{ description: "Discover project-defined tools through a live Bridge", argv: ["context", "read", "tools.custom", "--input", "{}"], input: {} }],
  async read(_request, ctx) {
    const discovery = await discoverProjectTools(ctx.flags ?? {}, ctx, ctx.requestId, { requireBridge: true });
    if (!discovery.ok) {
      throw contextErrorFromResult(discovery);
    }
    const tools = discovery.tools.filter((tool) => tool.source === "project") as ToolsCustomMetadata[];
    return { tools, count: tools.length, mode: discovery.mode };
  }
});
