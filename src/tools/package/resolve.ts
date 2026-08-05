import { runLivePackageResolve, type LiveResolveResult } from "./live-resolve.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageResolveRequest {}
export type PackageResolveResult = LiveResolveResult;

/** Resolve 调用、List fence 与 PackageInfo readback 共用公开 requestId root。 */
async function runPackageResolve(_request: PackageResolveRequest, context: ToolContext): Promise<PackageResolveResult> {
  return runLivePackageResolve<PackageResolveResult>(context, "resolve", {});
}

export default defineTool<PackageResolveRequest, PackageResolveResult>({
  name: "package.resolve", summary: "Resolve packages in the open Unity Editor and return PackageInfo proof.",
  description: "Resolves the selected open Unity project through Unity PackageManager and returns only a terminal PackageInfo readback. This operation requires one verified live Editor session.",
  group: "package", destructiveHint: true, sideEffects: ["session-affecting"], playModeSupport: "session-affecting", handler: "hybrid", supportStatus: "live-bridge", timeoutMs: 120_000,
  examples: [{ description: "Resolve packages in the open Editor.", argv: ["tools", "run", "package.resolve"] }],
  async execute(request, context) { return runPackageResolve(request, context); }
});
