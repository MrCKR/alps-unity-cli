import { handleCapabilitiesCommand } from "../../capabilities.js";
import type { ProjectToolRuntime } from "../../project-tool-discovery.js";
import type { ContextCatalogRuntime } from "../../contexts/catalog.js";
import type { BridgeRuntime } from "../../bridge/index.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";
import type { AnyJson } from "../../schema/any-json.js";

export interface DiagnoseCapabilitiesRequest {}

export interface DiagnoseCapabilitiesObject { [key: string]: AnyJson; }

export interface DiagnoseCapabilitiesResult {
  catalog: DiagnoseCapabilitiesObject;
  projectTools?: DiagnoseCapabilitiesObject;
  customContexts?: DiagnoseCapabilitiesObject;
  runtime?: DiagnoseCapabilitiesObject;
  extensions: DiagnoseCapabilitiesObject;
}

export default defineTool<DiagnoseCapabilitiesRequest, DiagnoseCapabilitiesResult>({
  name: "diagnose.capabilities",
  summary: "Summarize built-in CLI capabilities, tool/context catalog counts, extension availability, and optional selected Unity session capabilities.",
  description: "Summarize offline CLI capabilities and, when --instance selects a live project, include validated Unity session capabilities through the session supervisor.",
  group: "diagnose",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Read offline CLI capabilities", argv: ["tools", "run", "diagnose.capabilities", "--input", "{}"] },
    { description: "Include selected live capabilities", argv: ["tools", "run", "diagnose.capabilities", "--instance", "<projectPath>"] }
  ],
  /** 复用既有 capabilities 状态机,保持 offline/live/custom discovery 语义一致。 */
  async execute(request, ctx) {
    const flags = { ...ctx.flags };
    const runtime: BridgeRuntime & ProjectToolRuntime & ContextCatalogRuntime = {
      dataDir: ctx.dataDir,
      bridgeExecute: ctx.bridgeExecute,
      projectTools: ctx.projectTools,
      projectContexts: ctx.projectContexts
    };
    const result = await handleCapabilitiesCommand(flags, runtime, ctx.requestId);
    if (!result.ok) {
      throw StructuredToolError.from(result.error);
    }
    return result.data as DiagnoseCapabilitiesResult;
  }
});
