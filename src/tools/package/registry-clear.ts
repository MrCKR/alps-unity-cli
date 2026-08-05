import { resolve } from "node:path";
import { clearRegistry, readSnapshot } from "../../package-manager.js";
import { defineTool, type ToolContext } from "../define.js";

export interface PackageRegistryClearRequest {
  project?: string;
}

export interface PackageRegistryClearResult {
  projectPath: string;
  manifestPath: string;
  registryCleared?: boolean;
  dryRun?: boolean;
  plannedEffects?: Array<{ type: string; registry?: string }>;
}

export default defineTool<PackageRegistryClearRequest, PackageRegistryClearResult>({
  name: "package.registry.clear",
  summary: "Clear the primary registry URL from the project manifest.",
  description: "Remove the primary registry URL from Packages/manifest.json. Supports --dry-run.",
  group: "package",
  destructiveHint: true,
  supportsDryRun: true,
  sideEffects: ["manifest-change"],
  playModeSupport: "supported",
  handler: "offline",
  supportStatus: "cli-only",
  examples: [
    { description: "Clear the primary registry", argv: ["tools", "run", "package.registry.clear", "--input", "{}"] }
  ],
  async execute(request, context) {
    const projectPath = resolve(request.project ?? context.projectPath);
    const prepared = await clearRegistry(projectPath, { dryRun: true });
    if (context.execution.dryRun) return { projectPath, ...prepared } as PackageRegistryClearResult;
    await context.commitEffect?.();
    return { projectPath, ...(await clearRegistry(projectPath)) } as PackageRegistryClearResult;
  }
});
