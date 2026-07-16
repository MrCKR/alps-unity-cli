import { handleCapabilitiesCommand } from "../../capabilities.js";
import type { ProjectToolRuntime } from "../../project-tool-discovery.js";
import type { ContextCatalogRuntime } from "../../contexts/catalog.js";
import type { BridgeRuntime } from "../../bridge/index.js";
import { defineTool } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface DiagnoseCapabilitiesRequest {
  json?: boolean;
  format?: string;
  project?: string;
  includeCustom?: boolean;
}

export interface DiagnoseCapabilitiesObject {}

export interface DiagnoseCapabilitiesResult {
  command: string;
  mode: string;
  requiresUnity: boolean;
  static: DiagnoseCapabilitiesObject;
  projectTools?: DiagnoseCapabilitiesObject;
  customContexts?: DiagnoseCapabilitiesObject;
  runtime?: DiagnoseCapabilitiesObject;
  runtimeCustomContexts?: DiagnoseCapabilitiesObject;
  bridgeEndpoint?: string;
  contextDiscovery: DiagnoseCapabilitiesObject;
  toolsGroupFirstDiscovery: boolean;
  extensions: DiagnoseCapabilitiesObject;
}

function requestFlags(request: DiagnoseCapabilitiesRequest, base: Record<string, string | boolean> = {}): Record<string, string | boolean> {
  const flags: Record<string, string | boolean> = { ...base };
  if (request.json !== undefined) flags.json = request.json;
  if (request.format !== undefined) flags.format = request.format;
  if (request.project !== undefined) flags.project = request.project;
  if (request.includeCustom !== undefined) flags["include-custom"] = request.includeCustom;
  return flags;
}

export default defineTool<DiagnoseCapabilitiesRequest, DiagnoseCapabilitiesResult>({
  name: "diagnose.capabilities",
  summary: "Summarize built-in CLI capabilities, tool/context catalog counts, extension availability, and optional live Unity bridge capabilities when --bridge-endpoint is provided.",
  description: "Summarize built-in CLI capabilities, tool/context catalog counts, extension availability, and optional live Unity bridge capabilities when --bridge-endpoint is provided. Safe to run offline; runtime data is best-effort.",
  group: "diagnose",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Read offline CLI capabilities", argv: ["tools", "run", "diagnose.capabilities", "--input", "{}"] },
    { description: "Include live bridge capabilities", argv: ["tools", "run", "diagnose.capabilities", "--bridge-endpoint", "http://127.0.0.1:47395"] }
  ],
  /** 复用既有 capabilities 状态机,保持 offline/live/custom discovery 语义一致。 */
  async execute(request, ctx) {
    const flags = requestFlags(request, ctx.flags);
    const runtime: BridgeRuntime & ProjectToolRuntime & ContextCatalogRuntime = {
      dataDir: ctx.dataDir,
      bridgeExecute: ctx.bridgeExecute,
      bridgeEndpoint: ctx.bridgeEndpoint,
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
