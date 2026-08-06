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
  runtime?: DiagnoseCapabilitiesObject;
  extensions: DiagnoseCapabilitiesObject;
}

export default defineTool<DiagnoseCapabilitiesRequest, DiagnoseCapabilitiesResult>({
  name: "diagnose.capabilities",
  summary: "Summarize the selected Unity instance's complete Tool, Context, Extension, and runtime capabilities.",
  description: "Summarize the complete unified capability catalog of the selected live Unity instance. Package, Extension, and project-defined capabilities use the same catalog.",
  group: "diagnose",
  destructiveHint: false,
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Summarize the instance selected from CWD", argv: ["tools", "run", "diagnose.capabilities", "--input", "{}"] },
    { description: "Summarize an explicitly selected instance", argv: ["tools", "run", "diagnose.capabilities", "--instance", "<projectPath>"] }
  ],
  /** 复用统一 live catalog，来源只作为汇总维度。 */
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
