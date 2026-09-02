import { runLivePackageResolve, type LiveResolveResult } from "./live-resolve.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageResolveRequest {}
export type PackageResolveResult = LiveResolveResult;

/** Resolve 调用、List fence 与 PackageInfo readback 共用公开 requestId root。 */
async function runPackageResolve(_request: PackageResolveRequest, context: ToolContext): Promise<PackageResolveResult> {
  return runLivePackageResolve<PackageResolveResult>(context, "resolve", {});
}

export default defineTool<PackageResolveRequest, PackageResolveResult>({
  name: "package.resolve", summary: "Resolve packages in the live EditMode Editor and return durable PackageInfo proof.",
  description: "EditMode only. Dispatches Unity PackageManager Resolve once, waits for an explicit List fence, then requires fresh PackageInfo, manifest, and packages-lock snapshots to remain stable before returning. Response loss reattaches to the same durable root without replaying Resolve; timeout returns E_EXECUTION_TIMEOUT and requires business-state inspection.",
  group: "package", destructiveHint: true, sideEffects: ["package-resolve", "asset-import", "session-affecting"], playModeSupport: "editor-only", handler: "hybrid", supportStatus: "live-bridge", timeoutMs: 120_000,
  examples: [{ description: "Resolve packages in the open Editor.", argv: ["tools", "run", "package.resolve"] }],
  async execute(request, context) { return runPackageResolve(request, context); }
});
