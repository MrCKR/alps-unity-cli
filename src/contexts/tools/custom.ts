import { discoverProjectTools } from "../../project-tool-discovery.js";
import type { ToolMetadata } from "../../tools/metadata/index.js";
import { contextErrorFromResult, defineContext } from "../define.js";

export interface ToolsCustomRequest {}

export interface ToolsCustomResult {
  tools: ToolMetadata[];
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
  async read(_request, ctx) {
    const discovery = await discoverProjectTools(ctx.flags ?? {}, ctx, ctx.requestId, { requireBridge: true });
    if (!discovery.ok) {
      throw contextErrorFromResult(discovery);
    }
    return { tools: discovery.tools, count: discovery.tools.length, mode: discovery.mode };
  }
});
